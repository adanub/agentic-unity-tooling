using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Adanub.UnityMcp.Editor.Snapshot;

namespace Adanub.UnityMcp.Snapshot.Tests
{
    /// <summary>
    /// Headless checks for the engine-free snapshot reader (compile-linked from plugin/Editor/Snapshot).
    /// Synthetic snapshots, built here in the documented layout with their chunks written out of order,
    /// prove the reader's chunk gathering, offset rotation and error messages on known bytes; they share
    /// the reader's model of the format, so only a REAL capture proves the model. Pass one:
    ///
    ///   dotnet run --project tests/SnapshotReader.Tests -- &lt;capture.snap&gt; [editor-counts.json]
    ///
    /// The optional counts file maps native type names to the editor's own object counts taken in the
    /// same frame as the capture; every type it lists must match the snapshot's count.
    /// Exits non-zero on any failure.
    /// </summary>
    internal static class Program
    {
        private static int _checks;
        private static int _failures;

        private static int Main(string[] args)
        {
            string temp = Path.Combine(Path.GetTempPath(), "snapshot-reader-tests");
            Directory.CreateDirectory(temp);
            try
            {
                SyntheticSnapshotReads(temp);
                BrokenFilesFailPrecisely(temp);
            }
            finally
            {
                Directory.Delete(temp, true);
            }

            if (args.Length > 0)
                RealSnapshot(args[0], args.Length > 1 ? args[1] : null);
            else
                Console.WriteLine("(no capture given: the real-snapshot checks did not run)");

            Console.WriteLine($"{_checks} checks, {_failures} failures.");
            return _failures == 0 ? 0 : 1;
        }

        // ───────────────────────────── synthetic ─────────────────────────────

        private static SnapshotFixture Fixture()
        {
            var fixture = new SnapshotFixture();
            fixture.Single(SnapshotChapter.FormatVersion, BitConverter.GetBytes(18u));
            fixture.Strings(SnapshotChapter.NativeTypeNames, "Mesh", "Texture2D", "Ünïcödé name long enough to span several chunks");
            fixture.Constant(SnapshotChapter.NativeObjectTypeIndices, sizeof(int), Bytes(new[] { 0, 1, 1, 2 }));
            fixture.Constant(SnapshotChapter.NativeObjectSizes, sizeof(ulong), Bytes(new ulong[] { 10, 20, 30, 1UL << 40 }));
            fixture.Strings(SnapshotChapter.NativeObjectNames, "", "b", "cc", "ddd");
            return fixture;
        }

        private static void SyntheticSnapshotReads(string temp)
        {
            string path = Path.Combine(temp, "synthetic.snap");
            File.WriteAllBytes(path, Fixture().Build());
            Check(SnapshotFile.Check(path).State == SnapshotFileState.Complete, "a synthetic snapshot validates as complete");

            using var reader = SnapshotReader.Open(path);
            Check(reader.FormatVersion == 18, "the format version reads from its single-element chapter");
            Check(reader.ReadStrings(SnapshotChapter.NativeTypeNames).SequenceEqual(new[] { "Mesh", "Texture2D", "Ünïcödé name long enough to span several chunks" }),
                "variable-size strings read back across out-of-order chunks, UTF-8 intact");
            Check(reader.ReadInt32s(SnapshotChapter.NativeObjectTypeIndices).SequenceEqual(new[] { 0, 1, 1, 2 }), "an array of 4-byte elements reads back");
            Check(reader.ReadUInt64s(SnapshotChapter.NativeObjectSizes).SequenceEqual(new ulong[] { 10, 20, 30, 1UL << 40 }), "an array of 8-byte elements reads back, high bits intact");
            Check(reader.ReadStrings(SnapshotChapter.NativeObjectNames).SequenceEqual(new[] { "", "b", "cc", "ddd" }),
                "an empty first element and a last element ending at the total length read back");
            Check(reader.Count(SnapshotChapter.NativeObjectNames) == 4 && reader.Count(SnapshotChapter.NativeObjectSizes) == 4, "counts come from the chapter headers");
            Check(!reader.Has(SnapshotChapter.AllocatorNames) && reader.Count(SnapshotChapter.AllocatorNames) == 0, "an absent chapter reads as absent with no elements");
            Check(Fails(() => reader.ReadUInt64s(SnapshotChapter.NativeObjectTypeIndices), "stores elements of 4 bytes; this read expects 8"),
                "reading 4-byte elements as 8-byte ones fails naming both sizes");
            Check(Fails(() => reader.ReadStrings(SnapshotChapter.AllocatorNames), "AllocatorNames (slot 73) is absent"), "reading an absent chapter fails naming it");
            Check(Fails(() => reader.ReadStrings(SnapshotChapter.NativeObjectSizes), "strings are a variable-size array"), "reading numbers as strings fails naming the format");
        }

        private static void BrokenFilesFailPrecisely(string temp)
        {
            byte[] good = Fixture().Build();
            long directory = BitConverter.ToInt64(good, good.Length - sizeof(uint) - sizeof(long));
            long blockSection = BitConverter.ToInt64(good, (int)directory + 8);
            long firstBlock = BitConverter.ToInt64(good, (int)blockSection + 8);

            ExpectOpenFails(temp, "truncated", good.Take(good.Length - 7).ToArray(), "footer signature");
            ExpectOpenFails(temp, "header", Patched(good, 0, 0xDEADBEEFu), "header signature 0xDEADBEEF");
            ExpectOpenFails(temp, "directory", Patched(good, directory, 0x12345678u), "directory signature 0x12345678");
            ExpectOpenFails(temp, "block-version", Patched(good, blockSection, 0x0BADF00Du), "block-section version 0x0BADF00D");
            ExpectOpenFails(temp, "chunk", Patched(good, firstBlock + 16, long.MaxValue / 4), "chunk 0");
            ExpectOpenFails(temp, "empty", Array.Empty<byte>(), "shorter than a finished file");
            ExpectOpenFails(temp, "missing", null, "no file at the path");
        }

        private static void ExpectOpenFails(string temp, string name, byte[] bytes, string expected)
        {
            string path = Path.Combine(temp, name + ".snap");
            if (bytes != null)
                File.WriteAllBytes(path, bytes);
            Check(Fails(() => SnapshotReader.Open(path).Dispose(), expected), $"a {name} file fails to open with '{expected}'");
        }

        // ───────────────────────────── a real capture ─────────────────────────────

        private static void RealSnapshot(string path, string countsPath)
        {
            var clock = Stopwatch.StartNew();
            using var reader = SnapshotReader.Open(path);
            Console.WriteLine($"{path}: {reader.Length:N0} bytes, format version {reader.FormatVersion}, {reader.ChapterSlots} chapter slots");

            string[] typeNames = reader.ReadStrings(SnapshotChapter.NativeTypeNames);
            int[] typeIndices = reader.ReadInt32s(SnapshotChapter.NativeObjectTypeIndices);
            ulong[] sizes = reader.ReadUInt64s(SnapshotChapter.NativeObjectSizes);
            string[] names = reader.ReadStrings(SnapshotChapter.NativeObjectNames);
            long[] rootIds = reader.ReadInt64s(SnapshotChapter.NativeObjectRootIds);
            long[] instanceIds = reader.ReadInstanceIds();
            int objects = typeIndices.Length;
            Check(objects > 0 && typeNames.Length > 0, $"the capture holds native objects ({objects:N0}) and native types ({typeNames.Length:N0})");
            Check(sizes.Length == objects && names.Length == objects && rootIds.Length == objects && instanceIds.Length == objects,
                $"the five native-object chapters agree on their count ({objects:N0}; sizes {sizes.Length}, names {names.Length}, roots {rootIds.Length}, ids {instanceIds.Length})");
            Check(typeIndices.All(i => i >= 0 && i < typeNames.Length), "every native object's type index names a native type");
            Check(instanceIds.Distinct().Count() == objects, "every native object's instance ID is distinct");

            long[] roots = reader.ReadInt64s(SnapshotChapter.NativeRootIds);
            ulong[] rootSizes = reader.ReadUInt64s(SnapshotChapter.NativeRootAccumulatedSizes);
            string[] areas = reader.ReadStrings(SnapshotChapter.NativeRootAreaNames);
            string[] rootNames = reader.ReadStrings(SnapshotChapter.NativeRootObjectNames);
            Check(roots.Length > 0 && rootSizes.Length == roots.Length && areas.Length == roots.Length && rootNames.Length == roots.Length,
                $"the four root chapters agree on their count ({roots.Length:N0})");
            var rootSet = new HashSet<long>(roots);
            int rooted = rootIds.Count(id => id > 0);
            int resolved = rootIds.Count(id => id > 0 && rootSet.Contains(id));
            Check(rooted > 0 && resolved == rooted, $"every native object's non-zero root ID names a root ({resolved:N0} of {rooted:N0})");

            long[] allocationRoots = reader.ReadInt64s(SnapshotChapter.NativeAllocationRootIds);
            ulong[] allocationSizes = reader.ReadUInt64s(SnapshotChapter.NativeAllocationSizes);
            Check(allocationRoots.Length == allocationSizes.Length, $"the allocation chapters agree on their count ({allocationSizes.Length:N0})");

            if (reader.FormatVersion >= 14)
            {
                ulong[] gfxIds = reader.ReadUInt64s(SnapshotChapter.GraphicsResourceIds);
                ulong[] gfxSizes = reader.ReadUInt64s(SnapshotChapter.GraphicsResourceSizes);
                long[] gfxRoots = reader.ReadInt64s(SnapshotChapter.GraphicsResourceRootIds);
                Check(gfxSizes.Length == gfxIds.Length && gfxRoots.Length == gfxIds.Length, $"the graphics-resource chapters agree on their count ({gfxIds.Length:N0})");
                string[] allocators = reader.ReadStrings(SnapshotChapter.AllocatorNames);
                ulong[] used = reader.ReadUInt64s(SnapshotChapter.AllocatorUsedSizes);
                ulong[] reserved = reader.ReadUInt64s(SnapshotChapter.AllocatorReservedSizes);
                ulong[] allocations = reader.ReadUInt64s(SnapshotChapter.AllocatorAllocationCounts);
                Check(allocators.Length > 0 && used.Length == allocators.Length && reserved.Length == allocators.Length && allocations.Length == allocators.Length,
                    $"the allocator chapters agree on their count ({allocators.Length})");
                Check(used.Zip(reserved, (u, r) => u <= r).All(x => x), "no allocator uses more than it reserves");

                Console.WriteLine($"  graphics resources: {gfxIds.Length:N0}, {gfxSizes.Aggregate(0UL, (a, b) => a + b) / 1048576.0:N1} MB, {gfxRoots.Count(r => r > 0 && rootSet.Contains(r)):N0} with a known root");
                Console.WriteLine("  allocators by used size:");
                foreach (int i in Enumerable.Range(0, allocators.Length).OrderByDescending(i => used[i]).Take(8))
                    Console.WriteLine($"    {used[i] / 1048576.0,10:N1} MB used, {allocations[i],10:N0} allocations  {allocators[i]}");
            }

            Console.WriteLine("  native types by object count:");
            var byType = Enumerable.Range(0, objects).GroupBy(i => typeIndices[i])
                .Select(g => (Name: typeNames[g.Key], Count: g.Count(), Bytes: g.Aggregate(0UL, (a, i) => a + sizes[i])))
                .OrderByDescending(t => t.Count).ToList();
            foreach (var type in byType.Take(12))
                Console.WriteLine($"    {type.Count,8:N0} objects {type.Bytes / 1048576.0,10:N1} MB  {type.Name}");
            Console.WriteLine($"  read in {clock.Elapsed.TotalSeconds:N1} s");

            if (countsPath != null)
                CompareWithEditorCounts(countsPath, byType.ToDictionary(t => t.Name, t => t.Count));
        }

        // The editor's own counts, taken in the same frame as the capture, against the snapshot's.
        private static void CompareWithEditorCounts(string countsPath, Dictionary<string, int> snapshot)
        {
            var editor = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(countsPath));
            Check(editor is { Count: > 0 }, $"the editor counts file lists types ({editor?.Count ?? 0})");
            if (editor is null)
                return;
            // A managed class name need not be its native type's name, so a type only one side names is a
            // naming difference, listed rather than counted as a mismatch.
            var shared = editor.Keys.Where(snapshot.ContainsKey).ToList();
            var editorOnly = editor.Keys.Where(t => !snapshot.ContainsKey(t)).OrderBy(t => t).ToList();
            var mismatches = shared.Where(t => editor[t] != snapshot[t]).OrderByDescending(t => editor[t])
                .Select(t => $"{t}: editor {editor[t]}, snapshot {snapshot[t]}").ToList();
            foreach (string line in mismatches)
                Console.WriteLine($"  count mismatch - {line}");
            if (editorOnly.Count > 0)
                Console.WriteLine($"  named by the editor only ({editorOnly.Count}): {string.Join(", ", editorOnly)}");
            Check(shared.Count >= 20, $"the editor and the snapshot share enough type names to compare ({shared.Count})");
            Check(mismatches.Count == 0, $"every type both sides name has the same count ({shared.Count - mismatches.Count} of {shared.Count} types, {shared.Sum(t => editor[t]):N0} objects)");
        }

        // ───────────────────────────── helpers ─────────────────────────────

        private static byte[] Bytes<T>(T[] values) where T : struct
        {
            var bytes = new byte[Buffer.ByteLength(values)];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        private static byte[] Patched(byte[] source, long offset, uint value)
        {
            byte[] copy = (byte[])source.Clone();
            BitConverter.GetBytes(value).CopyTo(copy, offset);
            return copy;
        }

        private static byte[] Patched(byte[] source, long offset, long value)
        {
            byte[] copy = (byte[])source.Clone();
            BitConverter.GetBytes(value).CopyTo(copy, offset);
            return copy;
        }

        // Typed and message-checked: a check satisfied by any exception would pass on an incidental one.
        private static bool Fails(Action action, string expectedFragment)
        {
            try
            {
                action();
                return false;
            }
            catch (SnapshotFormatException ex)
            {
                if (ex.Message.Contains(expectedFragment, StringComparison.Ordinal))
                    return true;
                Console.WriteLine($"  (failed, but with: {ex.Message})");
                return false;
            }
        }

        private static void Check(bool condition, string label)
        {
            _checks++;
            if (condition)
                return;
            _failures++;
            Console.WriteLine($"FAIL: {label}");
        }
    }

    /// <summary>
    /// Builds a small snapshot container in the layout <see cref="SnapshotReader"/> documents: each
    /// chapter's data is its own block, stored in small chunks written to the file in REVERSE order, so a
    /// reader that assumed a block's chunks were contiguous would read garbage.
    /// </summary>
    internal sealed class SnapshotFixture
    {
        private const int ChunkSize = 8;
        private const int ChapterSlots = 93;

        private sealed class Chapter
        {
            public SnapshotChapter Slot;
            public ushort Format;
            public byte[] Data;
            public uint UintMeta;
            public ulong UlongMeta;
            public long[] Offsets;
        }

        private readonly List<Chapter> _chapters = new List<Chapter>();

        public void Single(SnapshotChapter slot, byte[] data) =>
            _chapters.Add(new Chapter { Slot = slot, Format = 1, Data = data, UintMeta = (uint)data.Length, UlongMeta = 0 });

        // The count sits in the ulong's low 32 bits; the high half carries other data, as in a real capture.
        public void Constant(SnapshotChapter slot, int elementSize, byte[] data) =>
            _chapters.Add(new Chapter { Slot = slot, Format = 2, Data = data, UintMeta = (uint)elementSize, UlongMeta = (0x00170002UL << 32) | (uint)(data.Length / elementSize) });

        public void Strings(SnapshotChapter slot, params string[] values)
        {
            var data = new List<byte>();
            var offsets = new long[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                offsets[i] = data.Count;
                data.AddRange(Encoding.UTF8.GetBytes(values[i]));
            }
            _chapters.Add(new Chapter { Slot = slot, Format = 3, Data = data.ToArray(), UintMeta = (uint)values.Length, Offsets = offsets });
        }

        public byte[] Build()
        {
            using var file = new MemoryStream();
            using var w = new BinaryWriter(file);
            w.Write(0xAEABCDCDu);

            var chunkTables = new List<long[]>();
            foreach (var chapter in _chapters)
            {
                int chunks = (chapter.Data.Length + ChunkSize - 1) / ChunkSize;
                var table = new long[chunks];
                for (int k = chunks - 1; k >= 0; k--)
                {
                    table[k] = file.Position;
                    int start = k * ChunkSize;
                    w.Write(chapter.Data, start, Math.Min(ChunkSize, chapter.Data.Length - start));
                }
                chunkTables.Add(table);
            }

            var blockAddresses = new long[_chapters.Count];
            for (int b = 0; b < _chapters.Count; b++)
            {
                blockAddresses[b] = file.Position;
                w.Write((ulong)ChunkSize);
                w.Write((ulong)_chapters[b].Data.Length);
                foreach (long chunk in chunkTables[b])
                    w.Write(chunk);
            }

            long blockSection = file.Position;
            w.Write(0x20170724u);
            w.Write(_chapters.Count);
            foreach (long address in blockAddresses)
                w.Write(address);

            var chapterAddresses = new long[ChapterSlots];
            for (int i = 0; i < _chapters.Count; i++)
            {
                var chapter = _chapters[i];
                chapterAddresses[(int)chapter.Slot] = file.Position;
                w.Write(chapter.Format);
                w.Write((uint)i);
                w.Write(chapter.UintMeta);
                if (chapter.Format == 3)
                {
                    // On disk: the header's ulong holds element 0's offset; the table holds the rest, then the total.
                    long[] o = chapter.Offsets;
                    w.Write((ulong)(o.Length > 0 ? o[0] : 0));
                    for (int k = 1; k < o.Length; k++)
                        w.Write(o[k]);
                    if (o.Length > 0)
                        w.Write((long)chapter.Data.Length);
                }
                else
                {
                    w.Write(chapter.UlongMeta);
                }
            }

            long directory = file.Position;
            w.Write(0xCDCDAEABu);
            w.Write(0x20170724u);
            w.Write((ulong)blockSection);
            w.Write(ChapterSlots);
            foreach (long address in chapterAddresses)
                w.Write(address);

            w.Write((ulong)directory);
            w.Write(0xABCDCDAEu);
            w.Flush();
            return file.ToArray();
        }
    }
}
