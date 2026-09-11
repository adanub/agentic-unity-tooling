using System;
using System.Collections.Generic;
using System.Linq;

namespace Adanub.UnityMcp.Editor.Snapshot
{
    /// <summary>
    /// What one snapshot holds, reduced to what a comparison needs: native objects by type and by
    /// instance ID, native allocations and GPU resources by owning root, and allocators. Engine-free.
    /// </summary>
    public sealed class SnapshotSummary
    {
        public string FilePath;
        public uint FormatVersion;

        /// <summary>False for a format older than 14, which records no GPU resources or allocators.</summary>
        public bool HasGpuAndAllocators;

        public readonly Dictionary<string, (long Count, ulong Bytes)> ObjectsByType = new Dictionary<string, (long Count, ulong Bytes)>();
        public readonly Dictionary<long, (string Type, string Name, ulong Bytes)> Objects = new Dictionary<long, (string Type, string Name, ulong Bytes)>();
        public readonly Dictionary<string, (long Count, ulong Bytes)> AllocationsByRoot = new Dictionary<string, (long Count, ulong Bytes)>();
        public readonly Dictionary<string, (long Count, ulong Bytes)> GpuByRoot = new Dictionary<string, (long Count, ulong Bytes)>();
        public readonly Dictionary<ulong, (string Root, ulong Bytes)> GpuResources = new Dictionary<ulong, (string Root, ulong Bytes)>();
        public readonly Dictionary<string, (ulong Used, ulong Reserved, ulong Allocations)> Allocators = new Dictionary<string, (ulong Used, ulong Reserved, ulong Allocations)>();
    }

    /// <summary>
    /// Compares two snapshots of one process: what grew, what shrank and what is new, each table ordered
    /// by the size of its change and cut to a limit the output reports. Owners are matched by NAME
    /// ("area: object" from the root table), since a root's ID need not survive between captures; native
    /// objects and GPU resources by their IDs, which do within one session. Engine-free.
    /// </summary>
    public static class SnapshotDiff
    {
        public const string Unrooted = "(unrooted)";
        public const string MissingRoot = "(root missing from the root table)";

        public static SnapshotSummary Read(string path)
        {
            using var reader = SnapshotReader.Open(path);
            var summary = new SnapshotSummary { FilePath = path, FormatVersion = reader.FormatVersion };

            long[] rootIds = reader.ReadInt64s(SnapshotChapter.NativeRootIds);
            string[] areas = reader.ReadStrings(SnapshotChapter.NativeRootAreaNames);
            string[] rootObjects = reader.ReadStrings(SnapshotChapter.NativeRootObjectNames);
            Agree(SnapshotChapter.NativeRootIds, rootIds.Length, (SnapshotChapter.NativeRootAreaNames, areas.Length), (SnapshotChapter.NativeRootObjectNames, rootObjects.Length));
            var roots = new Dictionary<long, string>(rootIds.Length);
            for (int i = 0; i < rootIds.Length; i++)
                roots[rootIds[i]] = string.IsNullOrEmpty(rootObjects[i]) ? areas[i] : $"{areas[i]}: {rootObjects[i]}";

            // A root ID of 0 or less is unrooted, as the Memory Profiler treats it.
            string RootName(long id) => id <= 0 ? Unrooted : roots.TryGetValue(id, out var name) ? name : MissingRoot;

            string[] typeNames = reader.ReadStrings(SnapshotChapter.NativeTypeNames);
            int[] typeIndices = reader.ReadInt32s(SnapshotChapter.NativeObjectTypeIndices);
            ulong[] sizes = reader.ReadUInt64s(SnapshotChapter.NativeObjectSizes);
            string[] names = reader.ReadStrings(SnapshotChapter.NativeObjectNames);
            long[] ids = reader.ReadInstanceIds();
            Agree(SnapshotChapter.NativeObjectTypeIndices, typeIndices.Length,
                (SnapshotChapter.NativeObjectSizes, sizes.Length), (SnapshotChapter.NativeObjectNames, names.Length), (SnapshotChapter.NativeObjectInstanceIds, ids.Length));
            for (int i = 0; i < typeIndices.Length; i++)
            {
                int t = typeIndices[i];
                if (t < 0 || t >= typeNames.Length)
                    throw new SnapshotFormatException($"native object {i} names type {t}; the snapshot has {typeNames.Length} types");
                Add(summary.ObjectsByType, typeNames[t], 1, sizes[i]);
                summary.Objects[ids[i]] = (typeNames[t], names[i], sizes[i]);
            }

            long[] allocationRoots = reader.ReadInt64s(SnapshotChapter.NativeAllocationRootIds);
            ulong[] allocationSizes = reader.ReadUInt64s(SnapshotChapter.NativeAllocationSizes);
            Agree(SnapshotChapter.NativeAllocationRootIds, allocationRoots.Length, (SnapshotChapter.NativeAllocationSizes, allocationSizes.Length));
            var byRootId = new Dictionary<long, (long Count, ulong Bytes)>();
            for (int i = 0; i < allocationRoots.Length; i++)
                Add(byRootId, Math.Max(allocationRoots[i], 0), 1, allocationSizes[i]);
            foreach (var pair in byRootId)
                Add(summary.AllocationsByRoot, RootName(pair.Key), pair.Value.Count, pair.Value.Bytes);

            summary.HasGpuAndAllocators = reader.Has(SnapshotChapter.GraphicsResourceIds) && reader.Has(SnapshotChapter.AllocatorNames);
            if (!summary.HasGpuAndAllocators)
                return summary;

            ulong[] gfxIds = reader.ReadUInt64s(SnapshotChapter.GraphicsResourceIds);
            ulong[] gfxSizes = reader.ReadUInt64s(SnapshotChapter.GraphicsResourceSizes);
            long[] gfxRoots = reader.ReadInt64s(SnapshotChapter.GraphicsResourceRootIds);
            Agree(SnapshotChapter.GraphicsResourceIds, gfxIds.Length, (SnapshotChapter.GraphicsResourceSizes, gfxSizes.Length), (SnapshotChapter.GraphicsResourceRootIds, gfxRoots.Length));
            for (int i = 0; i < gfxIds.Length; i++)
            {
                if (gfxSizes[i] == 0)
                    continue; // a zero-size resource holds nothing; the Memory Profiler skips it too
                string root = RootName(gfxRoots[i]);
                Add(summary.GpuByRoot, root, 1, gfxSizes[i]);
                summary.GpuResources[gfxIds[i]] = (root, gfxSizes[i]);
            }

            string[] allocators = reader.ReadStrings(SnapshotChapter.AllocatorNames);
            ulong[] used = reader.ReadUInt64s(SnapshotChapter.AllocatorUsedSizes);
            ulong[] reserved = reader.ReadUInt64s(SnapshotChapter.AllocatorReservedSizes);
            ulong[] counts = reader.ReadUInt64s(SnapshotChapter.AllocatorAllocationCounts);
            Agree(SnapshotChapter.AllocatorNames, allocators.Length,
                (SnapshotChapter.AllocatorUsedSizes, used.Length), (SnapshotChapter.AllocatorReservedSizes, reserved.Length), (SnapshotChapter.AllocatorAllocationCounts, counts.Length));
            for (int i = 0; i < allocators.Length; i++)
            {
                summary.Allocators.TryGetValue(allocators[i], out var prior);
                summary.Allocators[allocators[i]] = (prior.Used + used[i], prior.Reserved + reserved[i], prior.Allocations + counts[i]);
            }
            return summary;
        }

        /// <summary>
        /// The changes from <paramref name="before"/> to <paramref name="after"/>. Every table lists only
        /// what changed, largest change first, at most <paramref name="limit"/> rows, and says how many
        /// changed in all.
        /// </summary>
        public static Dictionary<string, object> Compare(SnapshotSummary before, SnapshotSummary after, int limit)
        {
            var result = new Dictionary<string, object>
            {
                { "before", Describe(before) },
                { "after", Describe(after) },
                { "totals", Totals(before, after) },
                { "objectsByType", Grouped(before.ObjectsByType, after.ObjectsByType, limit) },
                { "newObjects", NewObjects(before, after, limit) },
                { "nativeAllocationsByRoot", Grouped(before.AllocationsByRoot, after.AllocationsByRoot, limit) },
            };
            if (before.HasGpuAndAllocators && after.HasGpuAndAllocators)
            {
                result["gpuResourcesByRoot"] = Grouped(before.GpuByRoot, after.GpuByRoot, limit);
                result["newGpuResources"] = NewGpuResources(before, after, limit);
                result["allocators"] = Allocators(before, after, limit);
            }
            else
            {
                result["gpuAndAllocators"] = "not compared: a snapshot older than format version 14 records neither";
            }
            return result;
        }

        private static Dictionary<string, object> Describe(SnapshotSummary summary) => new Dictionary<string, object>
        {
            { "path", summary.FilePath },
            { "formatVersion", summary.FormatVersion },
        };

        private static Dictionary<string, object> Totals(SnapshotSummary before, SnapshotSummary after)
        {
            var totals = new Dictionary<string, object>
            {
                { "nativeObjects", Change(before.Objects.Count, after.Objects.Count, Sum(before.ObjectsByType), Sum(after.ObjectsByType)) },
                {
                    "nativeAllocations", Change(before.AllocationsByRoot.Values.Sum(v => v.Count), after.AllocationsByRoot.Values.Sum(v => v.Count),
                        Sum(before.AllocationsByRoot), Sum(after.AllocationsByRoot))
                },
            };
            if (before.HasGpuAndAllocators && after.HasGpuAndAllocators)
            {
                totals["gpuResources"] = Change(before.GpuResources.Count, after.GpuResources.Count, Sum(before.GpuByRoot), Sum(after.GpuByRoot));
                ulong usedBefore = before.Allocators.Values.Aggregate(0UL, (sum, v) => sum + v.Used);
                ulong usedAfter = after.Allocators.Values.Aggregate(0UL, (sum, v) => sum + v.Used);
                totals["allocatorsUsedBytes"] = new Dictionary<string, object> { { "before", usedBefore }, { "after", usedAfter }, { "delta", (long)usedAfter - (long)usedBefore } };
            }
            return totals;
        }

        private static Dictionary<string, object> Change(long countBefore, long countAfter, ulong bytesBefore, ulong bytesAfter) => new Dictionary<string, object>
        {
            { "countBefore", countBefore },
            { "countAfter", countAfter },
            { "countDelta", countAfter - countBefore },
            { "bytesBefore", bytesBefore },
            { "bytesAfter", bytesAfter },
            { "bytesDelta", (long)bytesAfter - (long)bytesBefore },
        };

        private static Dictionary<string, object> Grouped(Dictionary<string, (long Count, ulong Bytes)> before, Dictionary<string, (long Count, ulong Bytes)> after, int limit)
        {
            var rows = before.Keys.Union(after.Keys)
                .Select(key => (Key: key, Before: before.GetValueOrDefault(key), After: after.GetValueOrDefault(key)))
                .Where(r => r.Before.Count != r.After.Count || r.Before.Bytes != r.After.Bytes)
                .OrderByDescending(r => Math.Abs((double)r.After.Bytes - r.Before.Bytes))
                .ThenByDescending(r => Math.Abs(r.After.Count - r.Before.Count))
                .ToList();
            return Table(rows.Count, rows.Take(limit).Select(r =>
            {
                var row = new Dictionary<string, object> { { "name", r.Key } };
                foreach (var field in Change(r.Before.Count, r.After.Count, r.Before.Bytes, r.After.Bytes))
                    row[field.Key] = field.Value;
                return row;
            }));
        }

        private static Dictionary<string, object> NewObjects(SnapshotSummary before, SnapshotSummary after, int limit)
        {
            var added = after.Objects.Where(p => !before.Objects.ContainsKey(p.Key)).OrderByDescending(p => p.Value.Bytes).ToList();
            var table = Table(added.Count, added.Take(limit).Select(p => new Dictionary<string, object>
            {
                { "instanceId", p.Key },
                { "type", p.Value.Type },
                { "name", p.Value.Name },
                { "bytes", p.Value.Bytes },
            }));
            table["gone"] = before.Objects.Keys.Count(id => !after.Objects.ContainsKey(id));
            return table;
        }

        private static Dictionary<string, object> NewGpuResources(SnapshotSummary before, SnapshotSummary after, int limit)
        {
            var added = after.GpuResources.Where(p => !before.GpuResources.ContainsKey(p.Key)).OrderByDescending(p => p.Value.Bytes).ToList();
            var table = Table(added.Count, added.Take(limit).Select(p => new Dictionary<string, object>
            {
                { "id", p.Key },
                { "root", p.Value.Root },
                { "bytes", p.Value.Bytes },
            }));
            table["gone"] = before.GpuResources.Keys.Count(id => !after.GpuResources.ContainsKey(id));
            return table;
        }

        private static Dictionary<string, object> Allocators(SnapshotSummary before, SnapshotSummary after, int limit)
        {
            bool counted = before.Allocators.Values.Any(v => v.Allocations > 0) || after.Allocators.Values.Any(v => v.Allocations > 0);
            var rows = before.Allocators.Keys.Union(after.Allocators.Keys)
                .Select(key => (Key: key, Before: before.Allocators.GetValueOrDefault(key), After: after.Allocators.GetValueOrDefault(key)))
                .Where(r => r.Before.Used != r.After.Used || r.Before.Reserved != r.After.Reserved || r.Before.Allocations != r.After.Allocations)
                .OrderByDescending(r => Math.Abs((double)r.After.Used - r.Before.Used))
                .ToList();
            var table = Table(rows.Count, rows.Take(limit).Select(r =>
            {
                var row = new Dictionary<string, object>
                {
                    { "name", r.Key },
                    { "usedBefore", r.Before.Used },
                    { "usedAfter", r.After.Used },
                    { "usedDelta", (long)r.After.Used - (long)r.Before.Used },
                    { "reservedDelta", (long)r.After.Reserved - (long)r.Before.Reserved },
                };
                if (counted)
                    row["allocationsDelta"] = (long)r.After.Allocations - (long)r.Before.Allocations;
                return row;
            }));
            table["allocationCounts"] = counted ? "recorded" : "not recorded: every allocator reads 0 allocations in both snapshots";
            return table;
        }

        private static Dictionary<string, object> Table(int changed, IEnumerable<Dictionary<string, object>> rows)
        {
            var shown = rows.ToList();
            return new Dictionary<string, object>
            {
                { "changed", changed },
                { "shown", shown.Count },
                { "rows", shown },
            };
        }

        private static ulong Sum(Dictionary<string, (long Count, ulong Bytes)> table) => table.Values.Aggregate(0UL, (sum, v) => sum + v.Bytes);

        private static void Add<TKey>(Dictionary<TKey, (long Count, ulong Bytes)> into, TKey key, long count, ulong bytes)
        {
            into.TryGetValue(key, out var prior);
            into[key] = (prior.Count + count, prior.Bytes + bytes);
        }

        private static void Agree(SnapshotChapter first, int count, params (SnapshotChapter Chapter, int Count)[] others)
        {
            foreach (var other in others)
            {
                if (other.Count != count)
                    throw new SnapshotFormatException($"chapter {other.Chapter} has {other.Count} elements where {first} has {count}");
            }
        }
    }
}
