using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>
    /// Editor windows as windows: showing one by type, and the Game view's render settings. The Game
    /// view class is internal, so its size, VSync and display are read by reflection and each missing
    /// member is reported as absent rather than failing the whole read.
    /// </summary>
    public static class WindowCommands
    {
        [McpRoute("window/show",
            "Show an editor window by type name (GameView, SceneView, ProfilerWindow, FrameDebuggerWindow, or a project window " +
            "type), opening it if needed. Args: type (required), focus (bool, default false — bring the tab forward without " +
            "taking keyboard focus). Editor tab focus only; the editor process is never brought to the foreground.")]
        public static object Show(JObject args)
        {
            string typeName = args.Value<string>("type");
            if (string.IsNullOrEmpty(typeName))
                return new { error = "'type' (window type name) is required." };
            bool focus = args.Value<bool?>("focus") ?? false;

            var type = ResolveWindowType(typeName, out var candidates);
            if (type is null)
            {
                return new
                {
                    error = candidates.Count > 1
                        ? $"'{typeName}' matches several window types: {string.Join(", ", candidates)} — pass the full name."
                        : $"No EditorWindow type matches '{typeName}'.",
                };
            }

            var existing = Resources.FindObjectsOfTypeAll(type).OfType<EditorWindow>().FirstOrDefault(w => w != null);
            bool wasOpen = existing != null;
            EditorWindow window;
            if (wasOpen)
            {
                window = existing;
                // ShowTab brings a docked tab forward; Focus additionally takes keyboard focus.
                if (focus)
                    window.Focus();
                else
                    window.ShowTab();
            }
            else
            {
                window = EditorWindow.GetWindow(type, false, null, focus);
                if (window == null)
                    return new { error = $"EditorWindow.GetWindow returned nothing for '{type.FullName}'." };
                if (!focus)
                    window.ShowTab();
            }

            return new Dictionary<string, object>
            {
                { "type", type.Name },
                { "fullType", type.FullName },
                { "title", window.titleContent?.text },
                { "wasOpen", wasOpen },
                { "focused", EditorWindow.focusedWindow == window },
            };
        }

        [McpRoute("window/close",
            "Close an open editor window by type name — the counterpart of window/show, so a window opened for a measurement " +
            "is not left in the user's layout. Args: type (required). Closes every open instance of that type.")]
        public static object Close(JObject args)
        {
            string typeName = args.Value<string>("type");
            if (string.IsNullOrEmpty(typeName))
                return new { error = "'type' (window type name) is required." };

            var type = ResolveWindowType(typeName, out var candidates);
            if (type is null)
            {
                return new
                {
                    error = candidates.Count > 1
                        ? $"'{typeName}' matches several window types: {string.Join(", ", candidates)} — pass the full name."
                        : $"No EditorWindow type matches '{typeName}'.",
                };
            }

            int closed = 0;
            foreach (var window in Resources.FindObjectsOfTypeAll(type).OfType<EditorWindow>().ToList())
            {
                if (window == null)
                    continue;
                window.Close();
                closed++;
            }
            return new Dictionary<string, object>
            {
                { "type", type.Name },
                { "closed", closed },
            };
        }

        [McpRoute("gameview/info",
            "Game view render settings, the like-for-like conditions of a measurement: target render size, selected size entry, " +
            "VSync toggle, low-resolution-aspect mode, target display, window size; plus QualitySettings.vSyncCount and " +
            "Screen.width/height while playing. Args: none.")]
        public static object GameViewInfo(JObject args)
        {
            var editorAssembly = typeof(EditorWindow).Assembly;
            var gameViewType = editorAssembly.GetType("UnityEditor.GameView");
            if (gameViewType is null)
                return new { error = "UnityEditor.GameView not found (Unity version drift — update plugin/Editor/Commands/WindowCommands.cs)." };

            var playModeView = editorAssembly.GetType("UnityEditor.PlayModeView");
            var main = playModeView?.GetMethod("GetMainPlayModeView", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                ?.Invoke(null, null) as EditorWindow;

            var views = new List<object>();
            foreach (var obj in Resources.FindObjectsOfTypeAll(gameViewType))
            {
                if (obj is not EditorWindow view)
                    continue;
                var row = new Dictionary<string, object>
                {
                    { "isMain", view == main },
                    { "focused", EditorWindow.focusedWindow == view },
                    { "windowWidth", Mathf.RoundToInt(view.position.width) },
                    { "windowHeight", Mathf.RoundToInt(view.position.height) },
                };
                AddMember(row, view, "targetRenderSize", "targetRenderSize");
                AddMember(row, view, "vSyncEnabled", "vSyncEnabled");
                AddMember(row, view, "lowResolutionForAspectRatios", "lowResolutionForAspectRatios");
                AddMember(row, view, "targetDisplay", "targetDisplay");
                AddMember(row, view, "selectedSizeIndex", "selectedSizeIndex");
                if (row.TryGetValue("selectedSizeIndex", out var sizeIndex) && sizeIndex is int index)
                    row["selectedSize"] = SelectedSizeText(editorAssembly, index);
                views.Add(row);
            }

            var result = new Dictionary<string, object>
            {
                { "count", views.Count },
                { "gameViews", views },
                { "qualityVSyncCount", QualitySettings.vSyncCount },
                { "isPlaying", EditorApplication.isPlaying },
            };
            if (EditorApplication.isPlaying)
            {
                result["screenWidth"] = Screen.width;
                result["screenHeight"] = Screen.height;
            }
            return result;
        }

        private static void AddMember(Dictionary<string, object> row, object target, string member, string key)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = target.GetType();
            object value;
            try
            {
                var property = type.GetProperty(member, flags);
                if (property is not null)
                    value = property.GetValue(target);
                else
                {
                    var field = type.GetField(member, flags);
                    if (field is null)
                    {
                        row[key] = "<absent>";
                        return;
                    }
                    value = field.GetValue(target);
                }
            }
            catch (Exception ex)
            {
                row[key] = "<error: " + ex.GetBaseException().Message + ">";
                return;
            }

            row[key] = value switch
            {
                Vector2 v => new Dictionary<string, object> { { "width", Mathf.RoundToInt(v.x) }, { "height", Mathf.RoundToInt(v.y) } },
                Enum e => e.ToString(),
                _ => value,
            };
        }

        // GameViewSizes is a ScriptableSingleton; the selected entry's display text is what the
        // toolbar dropdown shows ("Full HD (1920x1080)", "Free Aspect").
        private static string SelectedSizeText(Assembly editorAssembly, int index)
        {
            try
            {
                var sizesType = editorAssembly.GetType("UnityEditor.GameViewSizes");
                var instance = sizesType?.BaseType?.GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                var group = sizesType?.GetProperty("currentGroup", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance);
                var size = group?.GetType().GetMethod("GetGameViewSize", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(group, new object[] { index });
                return size?.GetType().GetProperty("displayText", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(size) as string ?? "<absent>";
            }
            catch (Exception ex)
            {
                return "<error: " + ex.GetBaseException().Message + ">";
            }
        }

        // Editor-assembly windows first (GameView, ProfilerWindow are internal there), then every loaded
        // assembly so a project's own windows resolve by simple name too.
        private static Type ResolveWindowType(string name, out List<string> candidates)
        {
            candidates = new List<string>();
            var editorAssembly = typeof(EditorWindow).Assembly;
            var exact = editorAssembly.GetType(name) ?? editorAssembly.GetType("UnityEditor." + name);
            if (exact is not null && typeof(EditorWindow).IsAssignableFrom(exact))
                return exact;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray(); }
                foreach (var type in types)
                {
                    if (!typeof(EditorWindow).IsAssignableFrom(type) || type.IsAbstract)
                        continue;
                    if (string.Equals(type.FullName, name, StringComparison.OrdinalIgnoreCase))
                        return type;
                    if (string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase))
                        candidates.Add(type.FullName);
                }
            }
            if (candidates.Count == 1)
            {
                string only = candidates[0];
                return Type.GetType(only) ?? AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType(only)).FirstOrDefault(t => t is not null);
            }
            return null;
        }
    }
}
