using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Adanub.UnityMcp.Editor.Snapshot;
using Newtonsoft.Json.Linq;
using Unity.Profiling.Memory;
using UnityEditorInternal;
using UnityEngine;
using Profiler = UnityEngine.Profiling.Profiler;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>
    /// Memory snapshot capture of this editor. memory/snapshot starts a capture and returns its path at
    /// once; memory/snapshot-status (<see cref="MemorySnapshotStatusRoute"/>) reports when the file at
    /// that path is a finished capture. The file is the only durable record — there is no job state —
    /// so a capture started before a domain reload still reports finished after it.
    /// </summary>
    public static class MemorySnapshotCommands
    {
        // The Memory Profiler package's own default folder, under the project root, so its window lists
        // what this captures.
        private const string DefaultFolder = "MemoryCaptures";

        // The Memory Profiler window's default flags.
        private const CaptureFlags DefaultFlags = CaptureFlags.ManagedObjects | CaptureFlags.NativeObjects |
                                                  CaptureFlags.NativeAllocations | CaptureFlags.NativeAllocationSites |
                                                  CaptureFlags.NativeStackTraces;

        // Collection passes before an editor capture; see CollectGarbage.
        private const int MaxCollectionPasses = 6;

        [McpRoute("memory/snapshot",
            "Start a memory snapshot of this editor and return its path at once; then poll memory/snapshot-status (waitMs to long-poll) " +
            "until finished. Args: name (file name without extension; default <product>_<timestamp>; a name whose file exists or is being " +
            "captured returns that capture instead of starting another, so a retried call is harmless), folder (absolute or " +
            "project-relative, default MemoryCaptures - the Memory Profiler package's folder), flags (CaptureFlags names, default the " +
            "Memory Profiler window's: ManagedObjects, NativeObjects, NativeAllocations, NativeAllocationSites, NativeStackTraces), " +
            "collectGarbage (default true: collect managed garbage first, as the Memory Profiler window does for an editor capture).")]
        public static object Start(JObject args)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;

            string folderArg = args.Value<string>("folder");
            string folder = Path.GetFullPath(string.IsNullOrEmpty(folderArg)
                ? Path.Combine(projectRoot, DefaultFolder)
                : Path.IsPathRooted(folderArg) ? folderArg : Path.Combine(projectRoot, folderArg));

            string name = args.Value<string>("name");
            if (string.IsNullOrEmpty(name))
                name = $"{FileSafe(Application.productName)}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
            else if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return new { error = $"name '{name}' contains a character a file name cannot hold." };
            string path = Path.Combine(folder, name + SnapshotFile.Extension);

            CaptureFlags flags = DefaultFlags;
            if (args["flags"] is JArray flagNames)
            {
                flags = 0;
                foreach (var token in flagNames)
                {
                    if (!Enum.TryParse((string)token, true, out CaptureFlags flag))
                        return new { error = $"unknown capture flag '{token}'. Known: {string.Join(", ", Enum.GetNames(typeof(CaptureFlags)))}." };
                    flags |= flag;
                }
            }

            // Never a second capture onto one path: a retried call must not start another.
            if (MemorySnapshotStatusRoute.IsInFlight(path) || File.Exists(path))
                return Started(path, flags, false);

            // TakeSnapshot deletes an empty directory at its path and writes the capture in its place
            // (measured), so a path a directory holds is never handed to it.
            if (Directory.Exists(path))
                return new { error = $"a directory occupies {path}; choose another name or folder.", path };

            Directory.CreateDirectory(folder);
            if (args.Value<bool?>("collectGarbage") ?? true)
                CollectGarbage();

            MemorySnapshotStatusRoute.Begin(path);
            try
            {
                MemoryProfiler.TakeSnapshot(path, (reported, success) => MemorySnapshotStatusRoute.Finish(path, success, reported), flags);
            }
            catch (Exception ex)
            {
                MemorySnapshotStatusRoute.Finish(path, false, null);
                return new { error = $"TakeSnapshot refused the capture: {ex.GetBaseException().Message}", path };
            }
            return Started(path, flags, true);
        }

        private static Dictionary<string, object> Started(string path, CaptureFlags flags, bool started) => new Dictionary<string, object>
        {
            { "path", path },
            { "started", started },
            { "note", started ? "Capture started; poll memory/snapshot-status with this path." : "A capture of this path already exists or is running; nothing was started." },
            { "flags", flags.ToString() },
            // Recorded beside every capture: an unfocused editor may tick less often.
            { "editorApplicationActive", InternalEditorUtility.isApplicationActive },
        };

        // Several passes with a finaliser wait between them: an object whose finaliser is pending
        // survives the pass that finds it and can keep others alive into the capture. Stops early once
        // the managed heap has shrunk.
        private static void CollectGarbage()
        {
            long before = Profiler.GetMonoHeapSizeLong();
            for (int pass = 0; pass < MaxCollectionPasses; pass++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                if (Profiler.GetMonoHeapSizeLong() < before)
                    break;
            }
        }

        private static string FileSafe(string text)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(text.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }
    }

    /// <summary>
    /// Long-polling memory/snapshot-status. It reads only the file and this domain's record of the
    /// capture's finish callback, so it answers across a domain reload and never waits on the editor.
    /// Runs on the request thread, so this type holds no state whose initialiser touches Unity.
    /// </summary>
    public static class MemorySnapshotStatusRoute
    {
        private sealed class CaptureRecord
        {
            public bool Finished;
            public bool Success;
            public string ReportedPath;
        }

        // Captures this domain started, by full path. A domain reload empties it; the file alone
        // answers from then on.
        private static readonly Dictionary<string, CaptureRecord> _captures = new Dictionary<string, CaptureRecord>(StringComparer.OrdinalIgnoreCase);

        internal static void Begin(string path)
        {
            lock (_captures)
                _captures[Path.GetFullPath(path)] = new CaptureRecord();
        }

        internal static void Finish(string path, bool success, string reportedPath)
        {
            lock (_captures)
            {
                if (!_captures.TryGetValue(Path.GetFullPath(path), out var record))
                    return;
                record.Finished = true;
                record.Success = success;
                record.ReportedPath = reportedPath;
            }
        }

        internal static bool IsInFlight(string path)
        {
            lock (_captures)
                return _captures.TryGetValue(Path.GetFullPath(path), out var record) && !record.Finished;
        }

        [McpRoute("memory/snapshot-status",
            "Whether the snapshot at a path is a finished capture. Args: path (required, from memory/snapshot), waitMs (0-25000: long-poll " +
            "until finished or the wait elapses). Reads only the file, so it answers across a domain reload. state: missing | incomplete | " +
            "complete | invalid; finished is true once the file is complete, or once the capture failed (an invalid file, or the capture's " +
            "own callback reporting failure while its domain lives).",
            RunOnRequestThread = true)]
        public static object Status(JObject args)
        {
            string path = args.Value<string>("path");
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required (memory/snapshot returns it)." };
            path = Path.GetFullPath(path);
            int waitMs = Math.Clamp(args.Value<int?>("waitMs") ?? 0, 0, 25_000);

            var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
            while (true)
            {
                var check = SnapshotFile.Check(path);
                bool? callback = null;
                string reportedPath = null;
                lock (_captures)
                {
                    if (_captures.TryGetValue(path, out var record) && record.Finished)
                    {
                        callback = record.Success;
                        reportedPath = record.ReportedPath;
                    }
                }

                bool complete = check.State == SnapshotFileState.Complete;
                bool failed = check.State == SnapshotFileState.Invalid || callback == false;
                if (complete || failed || DateTime.UtcNow >= deadline)
                {
                    bool known;
                    lock (_captures)
                        known = _captures.ContainsKey(path);
                    return new Dictionary<string, object>
                    {
                        { "path", path },
                        { "state", check.State.ToString().ToLowerInvariant() },
                        { "finished", complete || failed },
                        { "failed", failed },
                        { "sizeBytes", check.Length },
                        { "detail", check.Detail },
                        {
                            "callback",
                            callback is null
                                ? known ? "pending" : "not started in this domain (a reload since, or another caller): the file alone answers"
                                : $"{(callback == true ? "succeeded" : "failed")} ({(string.IsNullOrEmpty(reportedPath) ? "no path reported" : reportedPath)})"
                        },
                    };
                }
                Thread.Sleep(250);
            }
        }
    }
}
