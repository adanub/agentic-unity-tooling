using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>
    /// Reflection bindings for the editor's Frame Debugger, which has no public API. Everything the
    /// window uses lives on the internal <c>UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility</c>
    /// (native-bound statics) and the internal <c>FrameDebuggerEventData</c> class the native side fills
    /// for ONE event at a time — the event at the current draw-call limit, populated when a play-mode
    /// view re-renders at that limit. Resolved lazily on the main thread; a member missing after a Unity
    /// upgrade fails with its name rather than a null-reference deep in a route.
    /// </summary>
    internal static class FrameDebuggerBindings
    {
        private const string UtilityTypeName = "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility";
        private const string EventDataTypeName = "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData";
        private const string EventStructTypeName = "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEvent";
        private const string PlayModeViewTypeName = "UnityEditor.PlayModeView";
        private const string FixHint = " (Unity version drift — update plugin/Editor/Commands/FrameDebuggerCommands.cs)";

        private static bool s_bound;
        private static string s_bindError;

        private static Type s_utility;
        private static Type s_eventData;
        private static PropertyInfo s_count;
        private static PropertyInfo s_limit;
        private static PropertyInfo s_eventsHash;
        private static PropertyInfo s_locallySupported;
        private static MethodInfo s_setEnabled;
        private static MethodInfo s_getFrameEvents;
        private static MethodInfo s_getFrameEventInfoName;
        private static MethodInfo s_getFrameEventObject;
        private static MethodInfo s_getBatchBreakCauseStrings;
        private static MethodInfo s_getFrameEventData;
        private static FieldInfo s_eventType;
        private static FieldInfo s_eventObj;
        private static MethodInfo s_getMainPlayModeView;
        private static MethodInfo s_setSceneRepaintDirty;
        private static string[] s_batchBreakCauses;

        /// <summary>Resolves every member once; returns null on success, else the failure naming the member.</summary>
        internal static string Bind()
        {
            if (s_bound)
                return s_bindError;
            s_bound = true;

            var editorAssembly = typeof(EditorWindow).Assembly;
            const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags instances = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            s_utility = editorAssembly.GetType(UtilityTypeName);
            s_eventData = editorAssembly.GetType(EventDataTypeName);
            var eventType = editorAssembly.GetType(EventStructTypeName);
            var playModeView = editorAssembly.GetType(PlayModeViewTypeName);
            if (s_utility is null || s_eventData is null || eventType is null || playModeView is null)
                return s_bindError = "Frame Debugger internals not found: " +
                                     string.Join(", ", new[]
                                     {
                                         s_utility is null ? UtilityTypeName : null,
                                         s_eventData is null ? EventDataTypeName : null,
                                         eventType is null ? EventStructTypeName : null,
                                         playModeView is null ? PlayModeViewTypeName : null,
                                     }.Where(n => n is not null)) + FixHint;

            s_count = s_utility.GetProperty("count", statics);
            s_limit = s_utility.GetProperty("limit", statics);
            s_eventsHash = s_utility.GetProperty("eventsHash", statics);
            s_locallySupported = s_utility.GetProperty("locallySupported", statics);
            s_setEnabled = s_utility.GetMethod("SetEnabled", statics, null, new[] { typeof(bool), typeof(int) }, null);
            s_getFrameEvents = s_utility.GetMethod("GetFrameEvents", statics, null, Type.EmptyTypes, null);
            s_getFrameEventInfoName = s_utility.GetMethod("GetFrameEventInfoName", statics, null, new[] { typeof(int) }, null);
            s_getFrameEventObject = s_utility.GetMethod("GetFrameEventObject", statics, null, new[] { typeof(int) }, null);
            s_getBatchBreakCauseStrings = s_utility.GetMethod("GetBatchBreakCauseStrings", statics, null, Type.EmptyTypes, null);
            s_getFrameEventData = s_utility.GetMethod("GetFrameEventData", statics, null, new[] { typeof(int), s_eventData }, null);
            s_eventType = eventType.GetField("m_Type", instances);
            s_eventObj = eventType.GetField("m_Obj", instances);
            s_getMainPlayModeView = playModeView.GetMethod("GetMainPlayModeView", statics, null, Type.EmptyTypes, null);
            s_setSceneRepaintDirty = typeof(EditorApplication).GetMethod("SetSceneRepaintDirty", statics, null, Type.EmptyTypes, null);

            var missing = new List<string>();
            if (s_count is null) missing.Add("FrameDebuggerUtility.count");
            if (s_limit is null) missing.Add("FrameDebuggerUtility.limit");
            if (s_eventsHash is null) missing.Add("FrameDebuggerUtility.eventsHash");
            if (s_locallySupported is null) missing.Add("FrameDebuggerUtility.locallySupported");
            if (s_setEnabled is null) missing.Add("FrameDebuggerUtility.SetEnabled(bool,int)");
            if (s_getFrameEvents is null) missing.Add("FrameDebuggerUtility.GetFrameEvents()");
            if (s_getFrameEventInfoName is null) missing.Add("FrameDebuggerUtility.GetFrameEventInfoName(int)");
            if (s_getFrameEventObject is null) missing.Add("FrameDebuggerUtility.GetFrameEventObject(int)");
            if (s_getBatchBreakCauseStrings is null) missing.Add("FrameDebuggerUtility.GetBatchBreakCauseStrings()");
            if (s_getFrameEventData is null) missing.Add("FrameDebuggerUtility.GetFrameEventData(int, FrameDebuggerEventData)");
            if (s_eventType is null) missing.Add("FrameDebuggerEvent.m_Type");
            if (s_eventObj is null) missing.Add("FrameDebuggerEvent.m_Obj");
            if (s_getMainPlayModeView is null) missing.Add("PlayModeView.GetMainPlayModeView()");
            if (s_setSceneRepaintDirty is null) missing.Add("EditorApplication.SetSceneRepaintDirty()");
            if (missing.Count > 0)
                return s_bindError = "Frame Debugger members not found: " + string.Join(", ", missing) + FixHint;
            return null;
        }

        internal static bool Enabled => FrameDebugger.enabled;
        internal static bool LocallySupported => (bool)s_locallySupported.GetValue(null);
        internal static int Count => (int)s_count.GetValue(null);
        internal static int EventsHash => (int)s_eventsHash.GetValue(null);

        internal static int Limit
        {
            get => (int)s_limit.GetValue(null);
            set => s_limit.SetValue(null, value);
        }

        internal static void SetEnabled(bool enabled, int remotePlayerGuid) =>
            s_setEnabled.Invoke(null, new object[] { enabled, remotePlayerGuid });

        internal static Array GetFrameEvents() => (Array)s_getFrameEvents.Invoke(null, null);
        internal static string GetFrameEventInfoName(int index) => (string)s_getFrameEventInfoName.Invoke(null, new object[] { index });
        internal static UnityEngine.Object GetFrameEventObject(int index) => (UnityEngine.Object)s_getFrameEventObject.Invoke(null, new object[] { index });
        internal static string EventTypeName(object frameEvent) => s_eventType.GetValue(frameEvent)?.ToString() ?? "?";
        internal static UnityEngine.Object EventObject(object frameEvent) => s_eventObj.GetValue(frameEvent) as UnityEngine.Object;

        internal static string[] BatchBreakCauses =>
            s_batchBreakCauses ??= (string[])s_getBatchBreakCauseStrings.Invoke(null, null) ?? Array.Empty<string>();

        internal static object NewEventData() => Activator.CreateInstance(s_eventData, true);

        internal static bool GetFrameEventData(int index, object eventData) =>
            (bool)s_getFrameEventData.Invoke(null, new[] { (object)index, eventData });

        /// <summary>A field of the filled event-data object by name, or null where this Unity build has none.</summary>
        internal static object EventDataField(object eventData, string field) =>
            s_eventData.GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(eventData);

        internal static EditorWindow MainPlayModeView() => s_getMainPlayModeView.Invoke(null, null) as EditorWindow;

        /// <summary>What the Frame Debugger window does after every limit change: mark the scene views
        /// dirty (internal) and repaint the play-mode view, so the frame re-renders at the limit.</summary>
        internal static void RequestRepaint()
        {
            s_setSceneRepaintDirty.Invoke(null, null);
            MainPlayModeView()?.Repaint();
            // A window's own Repaint() request alone did not re-render an unfocused editor's Game
            // view (measured: zero events after 8 s of polling), while a dock layout change did.
            // RepaintAllViews is the layout-change path made explicit.
            InternalEditorUtility.RepaintAllViews();
        }
    }

    /// <summary>
    /// Frame Debugger routes that complete within one main-thread hop: the event list and disable.
    /// The two that must wait for the play-mode view to re-render (enable, event-data) are in
    /// <see cref="FrameDebuggerWaitRoutes"/>. The session flags below persist across the domain reload
    /// a play-mode change can trigger, so disable can undo exactly what enable did.
    /// </summary>
    public static class FrameDebuggerCommands
    {
        private const string PausedByBridgeKey = "AdanubMcp.FrameDebugger.PausedByBridge";
        internal const int MaxEventDataIndices = 32;

        internal static bool PausedByBridge
        {
            get => SessionState.GetBool(PausedByBridgeKey, false);
            set => SessionState.SetBool(PausedByBridgeKey, value);
        }

        [McpRoute("framedebugger/events",
            "Events of the frame the Frame Debugger holds (framedebugger/enable first). Each row: index, type (SRPBatch, Mesh, " +
            "SkinOnGPU, InstancedMesh, ...), the profiler-marker path and the drawn object's name. Args: summary (bool — counts per " +
            "marker path + type instead of rows), match (substring or regex on path/object name), regex (bool), type (event type " +
            "name), offset (0), maxItems (200). A draw inside an 'SRPBatch' event was SRP-batched; a 'Mesh'/'SkinOnGPU' event was not.")]
        public static object Events(JObject args)
        {
            var bindError = FrameDebuggerBindings.Bind();
            if (bindError is not null)
                return new { error = bindError };
            if (!FrameDebuggerBindings.Enabled)
                return new { error = "The Frame Debugger is not enabled. Call framedebugger/enable first." };

            bool summary = args.Value<bool?>("summary") ?? false;
            string match = args.Value<string>("match");
            bool useRegex = args.Value<bool?>("regex") ?? false;
            string typeFilter = args.Value<string>("type");
            int offset = Math.Max(0, args.Value<int?>("offset") ?? 0);
            int maxItems = Math.Max(1, args.Value<int?>("maxItems") ?? 200);

            Func<string, bool> predicate = null;
            if (!string.IsNullOrEmpty(match))
            {
                try
                {
                    predicate = useRegex
                        ? new Func<string, bool>(new Regex(match, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).IsMatch)
                        : s => s.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0;
                }
                catch (ArgumentException ex)
                {
                    return new { error = "Invalid regex '" + match + "': " + ex.Message };
                }
            }

            var events = FrameDebuggerBindings.GetFrameEvents();
            var rows = new List<Dictionary<string, object>>();
            var perPathType = new Dictionary<(string path, string type), int>();
            var perType = new Dictionary<string, int>();
            int matched = 0;
            bool truncated = false;

            for (int i = 0; i < events.Length; i++)
            {
                var ev = events.GetValue(i);
                string type = FrameDebuggerBindings.EventTypeName(ev);
                if (typeFilter is not null && !string.Equals(type, typeFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                string path = FrameDebuggerBindings.GetFrameEventInfoName(i) ?? string.Empty;
                var obj = FrameDebuggerBindings.EventObject(ev);
                string objectName = obj != null ? obj.name : null;
                if (predicate is not null && !(predicate(path) || (objectName is not null && predicate(objectName))))
                    continue;

                matched++;
                perType[type] = perType.GetValueOrDefault(type) + 1;
                if (summary)
                {
                    var key = (path, type);
                    perPathType[key] = perPathType.GetValueOrDefault(key) + 1;
                    continue;
                }
                if (matched <= offset)
                    continue;
                if (rows.Count >= maxItems)
                {
                    truncated = true;
                    continue;
                }
                rows.Add(new Dictionary<string, object>
                {
                    { "index", i },
                    { "type", type },
                    { "path", path },
                    { "object", objectName },
                });
            }

            var result = new Dictionary<string, object>
            {
                { "eventCount", events.Length },
                { "limit", FrameDebuggerBindings.Limit },
                { "eventsHash", FrameDebuggerBindings.EventsHash },
                { "matched", matched },
                { "perType", perType },
            };
            if (summary)
            {
                result["summary"] = perPathType
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => new Dictionary<string, object> { { "path", kv.Key.path }, { "type", kv.Key.type }, { "count", kv.Value } })
                    .ToList();
            }
            else
            {
                result["offset"] = offset;
                result["truncated"] = truncated;
                result["items"] = rows;
            }
            return result;
        }

        [McpRoute("framedebugger/disable",
            "Disable the Frame Debugger and, if framedebugger/enable paused play mode, unpause it.")]
        public static object Disable(JObject args)
        {
            var bindError = FrameDebuggerBindings.Bind();
            if (bindError is not null)
                return new { error = bindError };

            bool wasEnabled = FrameDebuggerBindings.Enabled;
            if (wasEnabled)
            {
                FrameDebuggerBindings.RequestRepaint();
                FrameDebuggerBindings.SetEnabled(false, ProfilerDriver.connectedProfiler);
            }
            bool unpaused = false;
            if (PausedByBridge)
            {
                PausedByBridge = false;
                if (EditorApplication.isPlaying && EditorApplication.isPaused)
                {
                    EditorApplication.isPaused = false;
                    unpaused = true;
                }
            }
            return new Dictionary<string, object>
            {
                { "wasEnabled", wasEnabled },
                { "enabled", FrameDebuggerBindings.Enabled },
                { "unpaused", unpaused },
                { "isPlaying", EditorApplication.isPlaying },
                { "isPaused", EditorApplication.isPaused },
            };
        }

        // ─── Main-thread steps for the waiting routes ───

        internal static object EnableStep()
        {
            var bindError = FrameDebuggerBindings.Bind();
            if (bindError is not null)
                return new { error = bindError };
            if (!FrameDebuggerBindings.LocallySupported)
                return new { error = "The Frame Debugger is not supported on this editor's graphics device (it needs the multi-threaded renderer)." };

            var notes = new List<string>();
            var view = FrameDebuggerBindings.MainPlayModeView();
            if (view == null)
            {
                var gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                var open = gameViewType is not null ? Resources.FindObjectsOfTypeAll(gameViewType) : Array.Empty<UnityEngine.Object>();
                view = open.Length > 0 ? open[0] as EditorWindow : null;
            }
            if (view == null)
                return new { error = "No Game view is open — the Frame Debugger captures only what a play-mode view renders. Open one with window/show type=GameView." };

            // The debugger reads a VISIBLE view's frame; a Game view behind another tab renders nothing.
            view.ShowTab();

            bool paused = false;
            if (EditorApplication.isPlaying && !EditorApplication.isPaused)
            {
                EditorApplication.isPaused = true;
                PausedByBridge = true;
                paused = true;
                notes.Add("Paused play mode so the captured frame stays still; framedebugger/disable unpauses.");
            }

            if (!FrameDebuggerBindings.Enabled)
                FrameDebuggerBindings.SetEnabled(true, ProfilerDriver.connectedProfiler);
            FrameDebuggerBindings.RequestRepaint();

            return new Dictionary<string, object>
            {
                { "enabled", FrameDebuggerBindings.Enabled },
                { "pausedNow", paused },
                { "gameView", view.GetType().Name },
                { "notes", notes },
            };
        }

        /// <summary>One repaint request plus a count/hash read; the waiting route calls it until the hash holds still.</summary>
        internal static object[] EnablePoll()
        {
            FrameDebuggerBindings.RequestRepaint();
            return new object[] { FrameDebuggerBindings.Enabled, FrameDebuggerBindings.Count, FrameDebuggerBindings.EventsHash };
        }

        internal static object SetLimitStep(int limit)
        {
            if (!FrameDebuggerBindings.Enabled)
                return new { error = "The Frame Debugger is not enabled. Call framedebugger/enable first." };
            int count = FrameDebuggerBindings.Count;
            if (limit < 1 || limit > count)
                return new { error = $"Event index out of range: the frame has {count} events." };
            FrameDebuggerBindings.Limit = limit;
            FrameDebuggerBindings.RequestRepaint();
            return null;
        }

        /// <summary>Reads the event's data if the view has re-rendered at its limit; null until then.</summary>
        internal static object ReadEventDataStep(int index)
        {
            FrameDebuggerBindings.RequestRepaint();
            var data = FrameDebuggerBindings.NewEventData();
            if (!FrameDebuggerBindings.GetFrameEventData(index, data))
                return null;

            var events = FrameDebuggerBindings.GetFrameEvents();
            string type = index < events.Length ? FrameDebuggerBindings.EventTypeName(events.GetValue(index)) : "?";
            var obj = FrameDebuggerBindings.GetFrameEventObject(index);
            int cause = FrameDebuggerBindings.EventDataField(data, "m_BatchBreakCause") is int c ? c : -1;
            var causes = FrameDebuggerBindings.BatchBreakCauses;
            var mesh = FrameDebuggerBindings.EventDataField(data, "m_Mesh") as Mesh;

            var row = new Dictionary<string, object>
            {
                { "index", index },
                { "type", type },
                { "path", FrameDebuggerBindings.GetFrameEventInfoName(index) },
                { "object", obj != null ? obj.name : null },
                { "shader", FrameDebuggerBindings.EventDataField(data, "m_RealShaderName") },
                { "originalShader", FrameDebuggerBindings.EventDataField(data, "m_OriginalShaderName") },
                { "pass", FrameDebuggerBindings.EventDataField(data, "m_PassName") },
                { "lightMode", FrameDebuggerBindings.EventDataField(data, "m_PassLightMode") },
                { "subShaderIndex", FrameDebuggerBindings.EventDataField(data, "m_SubShaderIndex") },
                { "shaderPassIndex", FrameDebuggerBindings.EventDataField(data, "m_ShaderPassIndex") },
                { "keywords", FrameDebuggerBindings.EventDataField(data, "shaderKeywords") },
                { "mesh", mesh != null ? mesh.name : null },
                { "meshSubset", FrameDebuggerBindings.EventDataField(data, "m_MeshSubset") },
                { "vertexCount", FrameDebuggerBindings.EventDataField(data, "m_VertexCount") },
                { "indexCount", FrameDebuggerBindings.EventDataField(data, "m_IndexCount") },
                { "instanceCount", FrameDebuggerBindings.EventDataField(data, "m_InstanceCount") },
                { "drawCallCount", FrameDebuggerBindings.EventDataField(data, "m_DrawCallCount") },
                { "batchBreakCauseIndex", cause },
                { "batchBreakCause", cause >= 0 && cause < causes.Length ? causes[cause] : null },
                { "renderTarget", FrameDebuggerBindings.EventDataField(data, "m_RenderTargetName") },
                { "renderTargetWidth", FrameDebuggerBindings.EventDataField(data, "m_RenderTargetWidth") },
                { "renderTargetHeight", FrameDebuggerBindings.EventDataField(data, "m_RenderTargetHeight") },
            };
            AddStateFields(row, FrameDebuggerBindings.EventDataField(data, "m_DepthState"), "depth", "m_DepthWrite", "m_DepthFunc");
            AddStateFields(row, FrameDebuggerBindings.EventDataField(data, "m_RasterState"), "raster", "m_CullMode");
            AddStateFields(row, FrameDebuggerBindings.EventDataField(data, "m_BlendState"), "blend", "m_SrcBlend", "m_DstBlend", "m_BlendOp", "m_WriteMask");
            return row;
        }

        // Pipeline-state structs are read field by field so a renamed member drops one value rather
        // than the whole row.
        private static void AddStateFields(Dictionary<string, object> row, object state, string prefix, params string[] fields)
        {
            if (state is null)
                return;
            var type = state.GetType();
            foreach (var field in fields)
            {
                var info = type.GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (info is null)
                    continue;
                var value = info.GetValue(state);
                row[prefix + field.Substring(2)] = value is Enum ? value.ToString() : value;
            }
        }
    }

    /// <summary>
    /// Frame Debugger routes that wait for the play-mode view to re-render — cross-frame work, so they
    /// run on the request thread and take main-thread snapshots between waits, like compile/status.
    /// No static state here: the declaring type's static initialiser would run on the request thread.
    /// </summary>
    public static class FrameDebuggerWaitRoutes
    {
        [McpRoute("framedebugger/enable",
            "Enable the editor's Frame Debugger on the Game view (pauses play mode if playing) and wait until the frame's events are " +
            "in. Args: waitMs (default 5000, max 25000). Returns eventCount and eventsHash. Then read framedebugger/events; " +
            "framedebugger/disable when done.",
            RunOnRequestThread = true)]
        public static object Enable(JObject args)
        {
            int waitMs = Math.Clamp(args.Value<int?>("waitMs") ?? 5000, 250, 25_000);

            object started = McpBridgeServer.RunOnMainThread(FrameDebuggerCommands.EnableStep);
            if (started is not Dictionary<string, object> enable)
                return started;

            // The window waits four repaints before it trusts the event list; the equivalent here is a
            // count that is non-zero and a hash that holds still across two consecutive reads.
            var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
            int lastHash = int.MinValue;
            int stable = 0;
            int count = 0;
            bool enabled = false;
            while (true)
            {
                object polled = McpBridgeServer.RunOnMainThread(FrameDebuggerCommands.EnablePoll);
                if (polled is not object[] snap)
                    return polled;
                enabled = (bool)snap[0];
                count = (int)snap[1];
                int hash = (int)snap[2];
                if (enabled && count > 0)
                    stable = hash == lastHash ? stable + 1 : 0;
                lastHash = hash;
                if (stable >= 1 || DateTime.UtcNow >= deadline)
                    break;
                Thread.Sleep(100);
            }

            enable["enabled"] = enabled;
            enable["eventCount"] = count;
            enable["eventsHash"] = lastHash;
            enable["ready"] = enabled && count > 0 && stable >= 1;
            if (!enabled)
                enable["error"] = "The Frame Debugger did not enable. Is a Game view visible, and is the graphics device supported?";
            else if (count == 0)
                enable["error"] = $"No events arrived within {waitMs} ms: the Game view renders only while the editor APPLICATION has " +
                                  "OS focus (measured — no bridge-side repaint request substitutes for it). The user must click the " +
                                  "editor; the bridge never takes focus. The debugger stays enabled — read framedebugger/events after.";
            return enable;
        }

        [McpRoute("framedebugger/event-data",
            "Detail for chosen events: shader, pass, light mode, keywords, mesh, index/instance/draw counts, the BATCH BREAK CAUSE " +
            "(why this draw did not join the previous batch), render target, depth/raster/blend state. The data exists for one event " +
            "at a time and needs a Game view re-render per event, so pass a SHORT list. Args: indices (int[], required, max " +
            "32), waitMs (per event, default 2000, max 10000). Restores the draw-call limit afterwards.",
            RunOnRequestThread = true)]
        public static object EventData(JObject args)
        {
            var indices = args["indices"]?.ToObject<int[]>();
            if (indices is null || indices.Length == 0)
                return new { error = "'indices' (int[]) is required." };
            if (indices.Length > FrameDebuggerCommands.MaxEventDataIndices)
                return new { error = $"At most {FrameDebuggerCommands.MaxEventDataIndices} indices per call — each one costs a Game view re-render." };
            int waitMs = Math.Clamp(args.Value<int?>("waitMs") ?? 2000, 250, 10_000);

            object bound = McpBridgeServer.RunOnMainThread(() =>
            {
                var bindError = FrameDebuggerBindings.Bind();
                if (bindError is not null)
                    return new { error = bindError };
                if (!FrameDebuggerBindings.Enabled)
                    return new { error = "The Frame Debugger is not enabled. Call framedebugger/enable first." };
                return new object[] { FrameDebuggerBindings.Limit, FrameDebuggerBindings.Count };
            });
            if (bound is not object[] initial)
                return bound;
            int previousLimit = (int)initial[0];

            var items = new List<object>();
            var failures = new List<string>();
            foreach (int index in indices)
            {
                object set = McpBridgeServer.RunOnMainThread(() => FrameDebuggerCommands.SetLimitStep(index + 1));
                if (set is not null)
                {
                    failures.Add($"{index}: " + (set is Dictionary<string, object> d && d.TryGetValue("error", out var e) ? e : Describe(set)));
                    continue;
                }

                var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
                object row = null;
                while (true)
                {
                    row = McpBridgeServer.RunOnMainThread(() => FrameDebuggerCommands.ReadEventDataStep(index));
                    if (row is not null || DateTime.UtcNow >= deadline)
                        break;
                    Thread.Sleep(50);
                }
                if (row is null)
                    failures.Add($"{index}: no data within {waitMs} ms — the Game view did not re-render at that limit (it renders only while the editor application has OS focus)");
                else if (row is Dictionary<string, object>)
                    items.Add(row);
                else
                    return row; // the main-thread hop timed out or threw
            }

            McpBridgeServer.RunOnMainThread(() =>
            {
                FrameDebuggerBindings.Limit = previousLimit;
                FrameDebuggerBindings.RequestRepaint();
                return null;
            });

            var result = new Dictionary<string, object>
            {
                { "count", items.Count },
                { "items", items },
                { "restoredLimit", previousLimit },
            };
            if (failures.Count > 0)
                result["failures"] = failures;
            return result;
        }

        private static string Describe(object error)
        {
            var prop = error?.GetType().GetProperty("error");
            return prop?.GetValue(error)?.ToString() ?? error?.ToString() ?? "unknown error";
        }
    }
}
