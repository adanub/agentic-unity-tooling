using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;
using Profiler = UnityEngine.Profiling.Profiler;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>
    /// Profiler queries, read-only except <c>profiler/record</c> — the one route that changes
    /// state, and only the Profiler's own recording toggle (marked mutating on the server side).
    /// When nothing is recording, the frame-based routes return a clear explanatory message
    /// instead of failing.
    /// </summary>
    public static class ProfilerCommands
    {
        private const double BytesPerMB = 1024.0 * 1024.0;
        private static double Mb(long bytes) => Math.Round(bytes / BytesPerMB, 2);

        [McpRoute("profiler/stats", "Rendering stats (draw calls, batches, tris, verts, set-pass, frame/render time). Most meaningful in Play mode; no recording required.")]
        public static object Stats(JObject args)
        {
            try
            {
                var result = new Dictionary<string, object>
                {
                    { "batches", UnityStats.batches },
                    { "drawCalls", UnityStats.drawCalls },
                    { "setPassCalls", UnityStats.setPassCalls },
                    { "triangles", UnityStats.triangles },
                    { "vertices", UnityStats.vertices },
                    { "shadowCasters", UnityStats.shadowCasters },
                    { "renderTextureChanges", UnityStats.renderTextureChanges },
                    { "usedTextureMemoryBytes", UnityStats.usedTextureMemorySize },
                    { "usedTextureCount", UnityStats.usedTextureCount },
                    { "frameTimeMs", Math.Round(UnityStats.frameTime * 1000.0, 3) },
                    { "renderTimeMs", Math.Round(UnityStats.renderTime * 1000.0, 3) },
                    { "screenRes", UnityStats.screenRes },
                    { "isPlaying", EditorApplication.isPlaying },
                };
                if (!EditorApplication.isPlaying)
                    result["note"] = "Rendering stats are most meaningful in Play mode.";
                return result;
            }
            catch (Exception ex)
            {
                return new { error = "Failed to read UnityStats: " + ex.Message };
            }
        }

        [McpRoute("profiler/memory", "Memory usage: total allocated/reserved, Mono heap used/size + fragmentation, gfx driver, temp allocator. Bytes + MB.")]
        public static object Memory(JObject args)
        {
            long allocated = Profiler.GetTotalAllocatedMemoryLong();
            long reserved = Profiler.GetTotalReservedMemoryLong();
            long unused = Profiler.GetTotalUnusedReservedMemoryLong();
            long monoUsed = Profiler.GetMonoUsedSizeLong();
            long monoHeap = Profiler.GetMonoHeapSizeLong();
            long gfx = Profiler.GetAllocatedMemoryForGraphicsDriver();
            long temp = Profiler.GetTempAllocatorSize();

            return new Dictionary<string, object>
            {
                { "totalAllocatedMB", Mb(allocated) },
                { "totalReservedMB", Mb(reserved) },
                { "totalUnusedReservedMB", Mb(unused) },
                { "monoUsedMB", Mb(monoUsed) },
                { "monoHeapMB", Mb(monoHeap) },
                { "monoFragmentationPercent", monoHeap > 0 ? Math.Round((1.0 - (double)monoUsed / monoHeap) * 100.0, 1) : 0.0 },
                { "gfxDriverMB", Mb(gfx) },
                { "tempAllocatorMB", Mb(temp) },
                { "totalAllocatedBytes", allocated },
                { "monoUsedBytes", monoUsed },
                { "gfxDriverBytes", gfx },
            };
        }

        [McpRoute("profiler/record", "Start or stop Profiler recording (ProfilerDriver.enabled). Args: enabled (bool, required), clear (bool, drop the captured frames first), deepProfiling (bool, toggles Deep Profile — changing it triggers a script recompile). Returns the resulting state and frame range.")]
        public static object Record(JObject args)
        {
            var enabled = args.Value<bool?>("enabled");
            if (enabled is null)
                return new { error = "'enabled' (bool) is required." };

            var notes = new List<string>();
            if (args.Value<bool?>("clear") ?? false)
                ProfilerDriver.ClearAllFrames();

            var deep = args.Value<bool?>("deepProfiling");
            if (deep.HasValue && ProfilerDriver.deepProfiling != deep.Value)
            {
                ProfilerDriver.deepProfiling = deep.Value;
                notes.Add("Deep Profile changed — Unity recompiles scripts (domain reload) before it takes effect.");
            }

            ProfilerDriver.enabled = enabled.Value;
            if (enabled.Value && !EditorApplication.isPlaying && !ProfilerDriver.profileEditor)
                notes.Add("Not in Play mode and 'Profile Editor' is off — no frames will be captured until Play starts.");

            var result = new Dictionary<string, object>
            {
                { "enabled", ProfilerDriver.enabled },
                { "deepProfiling", ProfilerDriver.deepProfiling },
                { "profileEditor", ProfilerDriver.profileEditor },
                { "isPlaying", EditorApplication.isPlaying },
                { "firstFrame", ProfilerDriver.firstFrameIndex },
                { "lastFrame", ProfilerDriver.lastFrameIndex },
            };
            if (notes.Count > 0)
                result["notes"] = notes;
            return result;
        }

        [McpRoute("profiler/frame-data", "CPU timing hierarchy for a captured frame. Args: frameIndex (default latest), maxItems (30), minTimeMs (0), threadIndex (0=main), maxDepth (3), match + regex (find samples by name anywhere in the tree, each reported with its ancestor path). Requires the Profiler to be recording.")]
        public static object FrameData(JObject args)
        {
            // Captured frames outlive both play mode and the recording toggle — reading them after
            // play has exited (or with Record off) is the NORMAL way to inspect a frame, so the only
            // precondition is that frames exist.
            int last = ProfilerDriver.lastFrameIndex;
            if (last < 0)
                return new { error = "No profiler frames have been captured. Start recording (profiler/record enabled=true) and run the scene, then retry." };

            int frameIndex = args.Value<int?>("frameIndex") ?? last;
            int maxItems = args.Value<int?>("maxItems") ?? 30;
            float minTimeMs = args.Value<float?>("minTimeMs") ?? 0f;
            int threadIndex = args.Value<int?>("threadIndex") ?? 0;
            int maxDepth = args.Value<int?>("maxDepth") ?? 3;
            string match = args.Value<string>("match");
            bool useRegex = args.Value<bool?>("regex") ?? false;

            if (frameIndex < ProfilerDriver.firstFrameIndex || frameIndex > ProfilerDriver.lastFrameIndex)
                return new
                {
                    error = $"Frame {frameIndex} out of range [{ProfilerDriver.firstFrameIndex}, {ProfilerDriver.lastFrameIndex}].",
                };

            using (var view = ProfilerDriver.GetHierarchyFrameDataView(
                frameIndex, threadIndex,
                HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                HierarchyFrameDataView.columnTotalTime, false))
            {
                if (view is null || !view.valid)
                    return new { error = $"No valid frame data for frame {frameIndex}, thread {threadIndex}." };

                var items = new List<Dictionary<string, object>>();
                var children = new List<int>();
                view.GetItemChildren(view.GetRootItemID(), children);

                var result = new Dictionary<string, object>
                {
                    { "frameIndex", frameIndex },
                    { "threadName", view.threadName },
                    { "frameTimeMs", Math.Round(view.frameTimeMs, 3) },
                    { "frameGpuMs", Math.Round(view.frameGpuTimeMs, 3) },
                    { "frameFps", Math.Round(view.frameFps, 1) },
                    { "sampleCount", view.sampleCount },
                    { "firstFrame", ProfilerDriver.firstFrameIndex },
                    { "lastFrame", ProfilerDriver.lastFrameIndex },
                    { "recording", ProfilerDriver.enabled },
                };

                if (!string.IsNullOrEmpty(match))
                {
                    // A name search walks the WHOLE tree: the samples worth finding (a custom
                    // ProfilerMarker inside the render loop) sit far below any sensible depth cap,
                    // and a depth-capped miss is indistinguishable from the marker not firing.
                    Func<string, bool> predicate;
                    try
                    {
                        predicate = useRegex
                            ? new Func<string, bool>(new Regex(match, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).IsMatch)
                            : n => n.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    catch (ArgumentException ex)
                    {
                        return new { error = "Invalid regex '" + match + "': " + ex.Message };
                    }

                    var path = new List<string>();
                    int scanned = 0;
                    bool truncated = CollectMatches(view, children, items, maxItems, predicate, path, ref scanned);
                    result["match"] = match;
                    result["scannedSamples"] = scanned;
                    result["truncated"] = truncated;
                }
                else
                {
                    CollectHierarchy(view, children, items, maxItems, minTimeMs, 0, maxDepth);
                    result["maxDepth"] = maxDepth;
                }

                result["itemCount"] = items.Count;
                result["items"] = items;
                return result;
            }
        }

        [McpRoute("profiler/analyze", "Combined snapshot: memory + (Play-mode) rendering stats + (if recording) CPU hotspots + scene complexity, with optimisation suggestions.")]
        public static object Analyze(JObject args)
        {
            var result = new Dictionary<string, object>();
            var suggestions = new List<string>();

            // Memory
            long monoUsed = Profiler.GetMonoUsedSizeLong();
            long monoHeap = Profiler.GetMonoHeapSizeLong();
            long gfx = Profiler.GetAllocatedMemoryForGraphicsDriver();
            result["memory"] = new Dictionary<string, object>
            {
                { "totalAllocatedMB", Mb(Profiler.GetTotalAllocatedMemoryLong()) },
                { "monoUsedMB", Mb(monoUsed) },
                { "monoHeapMB", Mb(monoHeap) },
                { "gfxDriverMB", Mb(gfx) },
            };
            if (monoHeap > 0 && (double)monoUsed / monoHeap < 0.5)
                suggestions.Add($"Mono heap is fragmented ({Mb(monoUsed)}MB used of {Mb(monoHeap)}MB). Reduce per-frame allocations so the heap can shrink.");
            if (Mb(gfx) > 512)
                suggestions.Add($"High graphics driver memory ({Mb(gfx)}MB). Review texture sizes, compression, and render textures.");

            // Rendering stats (Play mode)
            if (EditorApplication.isPlaying)
            {
                try
                {
                    int setPass = UnityStats.setPassCalls;
                    int batches = UnityStats.batches;
                    int tris = UnityStats.triangles;
                    result["rendering"] = new Dictionary<string, object>
                    {
                        { "batches", batches },
                        { "drawCalls", UnityStats.drawCalls },
                        { "setPassCalls", setPass },
                        { "triangles", tris },
                        { "vertices", UnityStats.vertices },
                        { "frameTimeMs", Math.Round(UnityStats.frameTime * 1000.0, 3) },
                    };
                    if (setPass > 50) suggestions.Add($"High SetPass call count ({setPass}). Fewer unique materials, GPU instancing, or SRP Batcher.");
                    if (tris > 500000) suggestions.Add($"High triangle count ({tris}). Consider LODs, mesh simplification, or culling.");
                }
                catch { /* stats unavailable */ }
            }
            else
            {
                result["rendering"] = new Dictionary<string, object> { { "note", "Enter Play mode for rendering stats." } };
            }

            // CPU hotspots (if recording)
            if (ProfilerDriver.enabled && ProfilerDriver.lastFrameIndex >= 0)
            {
                try
                {
                    using (var view = ProfilerDriver.GetHierarchyFrameDataView(
                        ProfilerDriver.lastFrameIndex, 0,
                        HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                        HierarchyFrameDataView.columnSelfTime, false))
                    {
                        if (view is not null && view.valid)
                        {
                            var hotspots = new List<Dictionary<string, object>>();
                            var children = new List<int>();
                            view.GetItemChildren(view.GetRootItemID(), children);
                            CollectHotspots(view, children, hotspots, 0, 4);
                            hotspots.Sort((a, b) => ((double)b["selfMs"]).CompareTo((double)a["selfMs"]));
                            if (hotspots.Count > 8) hotspots.RemoveRange(8, hotspots.Count - 8);
                            result["hotspots"] = hotspots;

                            if (view.frameTimeMs > 33.3)
                                suggestions.Add($"Frame time {view.frameTimeMs:F1}ms exceeds the 30fps budget (33.3ms). See hotspots.");
                        }
                    }
                }
                catch { /* ignore */ }
            }
            else
            {
                result["profiler"] = new Dictionary<string, object> { { "note", "Profiler not recording — enable it for CPU hotspots." } };
            }

            // Scene complexity
            var renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            int realtimeShadow = 0;
            foreach (var l in lights)
                if (l.shadows != LightShadows.None && l.lightmapBakeType == LightmapBakeType.Realtime) realtimeShadow++;
            result["sceneComplexity"] = new Dictionary<string, object>
            {
                { "rendererCount", renderers.Length },
                { "lightCount", lights.Length },
                { "realtimeShadowLights", realtimeShadow },
            };
            if (realtimeShadow > 2)
                suggestions.Add($"{realtimeShadow} realtime shadow-casting lights. Bake shadows or limit realtime shadow lights.");

            result["suggestions"] = suggestions;
            return result;
        }

        private static void CollectHierarchy(HierarchyFrameDataView view, List<int> ids,
            List<Dictionary<string, object>> output, int maxItems, float minTimeMs, int depth, int maxDepth)
        {
            if (depth > maxDepth) return;
            foreach (int id in ids)
            {
                if (output.Count >= maxItems) break;

                float total = view.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnTotalTime);
                if (depth > 0 && total < minTimeMs) continue;
                float self = view.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnSelfTime);

                output.Add(new Dictionary<string, object>
                {
                    { "name", view.GetItemName(id) },
                    { "depth", depth },
                    { "totalMs", Math.Round(total, 3) },
                    { "selfMs", Math.Round(self, 3) },
                    { "calls", view.GetItemColumnData(id, HierarchyFrameDataView.columnCalls) },
                    { "gcAlloc", view.GetItemColumnData(id, HierarchyFrameDataView.columnGcMemory) },
                });

                if (depth < maxDepth && view.HasItemChildren(id))
                {
                    var kids = new List<int>();
                    view.GetItemChildren(id, kids);
                    CollectHierarchy(view, kids, output, maxItems, minTimeMs, depth + 1, maxDepth);
                }
            }
        }

        // Depth-first over every sample; a hit carries its ancestor chain so the SAME marker under two
        // cameras (or two call sites) stays attributable. Returns true when maxItems cut the walk short.
        private static bool CollectMatches(HierarchyFrameDataView view, List<int> ids,
            List<Dictionary<string, object>> output, int maxItems, Func<string, bool> predicate,
            List<string> path, ref int scanned)
        {
            foreach (int id in ids)
            {
                if (output.Count >= maxItems)
                    return true;
                scanned++;
                string name = view.GetItemName(id);
                if (predicate(name))
                {
                    output.Add(new Dictionary<string, object>
                    {
                        { "name", name },
                        { "depth", path.Count },
                        { "path", string.Join(" / ", path) },
                        { "totalMs", Math.Round(view.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnTotalTime), 3) },
                        { "selfMs", Math.Round(view.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnSelfTime), 3) },
                        { "calls", view.GetItemColumnData(id, HierarchyFrameDataView.columnCalls) },
                        { "gcAlloc", view.GetItemColumnData(id, HierarchyFrameDataView.columnGcMemory) },
                    });
                }
                if (view.HasItemChildren(id))
                {
                    var kids = new List<int>();
                    view.GetItemChildren(id, kids);
                    path.Add(name);
                    bool cut = CollectMatches(view, kids, output, maxItems, predicate, path, ref scanned);
                    path.RemoveAt(path.Count - 1);
                    if (cut)
                        return true;
                }
            }
            return false;
        }

        private static void CollectHotspots(HierarchyFrameDataView view, List<int> ids,
            List<Dictionary<string, object>> output, int depth, int maxDepth)
        {
            if (depth > maxDepth) return;
            foreach (int id in ids)
            {
                float self = view.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnSelfTime);
                if (self > 0.1f)
                {
                    output.Add(new Dictionary<string, object>
                    {
                        { "name", view.GetItemName(id) },
                        { "selfMs", Math.Round(self, 3) },
                        { "totalMs", Math.Round(view.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnTotalTime), 3) },
                        { "gcAlloc", view.GetItemColumnData(id, HierarchyFrameDataView.columnGcMemory) },
                    });
                }
                if (depth < maxDepth && view.HasItemChildren(id))
                {
                    var kids = new List<int>();
                    view.GetItemChildren(id, kids);
                    CollectHotspots(view, kids, output, depth + 1, maxDepth);
                }
            }
        }
    }
}
