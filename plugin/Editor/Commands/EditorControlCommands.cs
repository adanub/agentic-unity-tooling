using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>
    /// The editor's own controls — play mode, the open scene, a menu item — so a measurement or a
    /// diagnostic can be driven without a hand on the editor. All three change editor state and are
    /// marked mutating on the server side. None of them ever answers a dialog: a scene switch that
    /// would prompt to save is refused with the dirty scenes named, and a menu item that opens a modal
    /// leaves it for the user.
    /// </summary>
    public static class EditorControlCommands
    {
        [McpRoute("editor/playmode",
            "Play-mode control. Args: action (required) — 'play' (enter play mode), 'stop' (exit), 'pause', 'unpause', 'step' " +
            "(advance one frame while paused). Entering or leaving play takes effect on a later editor frame: wait for it with " +
            "editor/playmode-wait, passing this result's editEntries, which blocks until the editor's play-mode event says " +
            "the transition completed.")]
        public static object PlayMode(JObject args)
        {
            string action = args.Value<string>("action")?.ToLowerInvariant();
            if (string.IsNullOrEmpty(action))
                return new { error = "'action' is required: play | stop | pause | unpause | step." };
            if (EditorApplication.isCompiling)
                return new { error = "The editor is compiling; play-mode changes are refused until it finishes." };

            string note = null;
            switch (action)
            {
                case "play":
                    if (EditorApplication.isPlaying)
                        note = "Already in play mode.";
                    else
                        EditorApplication.EnterPlaymode();
                    break;
                case "stop":
                    if (!EditorApplication.isPlaying)
                        note = "Not in play mode.";
                    else
                        EditorApplication.ExitPlaymode();
                    break;
                case "pause":
                    if (!EditorApplication.isPlaying)
                        return new { error = "Not in play mode; nothing to pause." };
                    EditorApplication.isPaused = true;
                    break;
                case "unpause":
                    if (!EditorApplication.isPlaying)
                        return new { error = "Not in play mode; nothing to unpause." };
                    EditorApplication.isPaused = false;
                    break;
                case "step":
                    if (!EditorApplication.isPlaying)
                        return new { error = "Not in play mode; nothing to step." };
                    EditorApplication.Step();
                    break;
                default:
                    return new { error = $"Unknown action '{action}': play | stop | pause | unpause | step." };
            }

            var result = new Dictionary<string, object>
            {
                { "action", action },
                { "isPlaying", EditorApplication.isPlaying },
                { "isPaused", EditorApplication.isPaused },
                { "isPlayingOrWillChangePlaymode", EditorApplication.isPlayingOrWillChangePlaymode },
                { "editEntries", PlayModeTransitions.EditEntries },
            };
            if (note is not null)
                result["note"] = note;
            return result;
        }

        [McpRoute("scene/open",
            "Open a scene asset in the editor (edit mode only). Args: path (required, e.g. Assets/Scenes/Foo.unity), mode " +
            "('single' default — replaces the open scenes; 'additive'). Refused, with the scenes named, when a loaded scene has " +
            "unsaved changes that 'single' would discard: nothing here ever saves or discards for the user.")]
        public static object OpenScene(JObject args)
        {
            string path = args.Value<string>("path");
            if (string.IsNullOrEmpty(path))
                return new { error = "'path' (scene asset path) is required." };
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return new { error = "Scenes are opened in edit mode only; exit play mode first (editor/playmode action=stop)." };

            string modeArg = (args.Value<string>("mode") ?? "single").ToLowerInvariant();
            OpenSceneMode mode;
            switch (modeArg)
            {
                case "single": mode = OpenSceneMode.Single; break;
                case "additive": mode = OpenSceneMode.Additive; break;
                default: return new { error = $"Unknown mode '{modeArg}': single | additive." };
            }

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
                return new { error = $"No scene asset at '{path}'." };

            if (mode == OpenSceneMode.Single)
            {
                var dirty = new List<string>();
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (scene.isDirty)
                        dirty.Add(string.IsNullOrEmpty(scene.path) ? "(untitled)" : scene.path);
                }
                if (dirty.Count > 0)
                    return new { error = "Refused: a loaded scene has unsaved changes that opening in 'single' mode would discard: " + string.Join(", ", dirty) + ". Save or revert it in the editor first." };
            }

            var opened = EditorSceneManager.OpenScene(path, mode);
            return new Dictionary<string, object>
            {
                { "name", opened.name },
                { "path", opened.path },
                { "isLoaded", opened.isLoaded },
                { "mode", modeArg },
                { "sceneCount", SceneManager.sceneCount },
                { "rootCount", opened.isLoaded ? opened.rootCount : 0 },
            };
        }

        [McpRoute("editor/menu-item",
            "Execute an editor menu item by its menu path (e.g. 'Assets/Reimport All', 'Window/Analysis/Profiler'). Args: path " +
            "(required). Returns whether the editor found and ran it. An item that opens a dialog leaves the dialog for the user.")]
        public static object MenuItem(JObject args)
        {
            string path = args.Value<string>("path");
            if (string.IsNullOrEmpty(path))
                return new { error = "'path' (menu path) is required." };

            bool enabled;
            try { enabled = Menu.GetEnabled(path); }
            catch (Exception) { enabled = true; } // not every item registers with Menu; ExecuteMenuItem is the authority

            bool executed = EditorApplication.ExecuteMenuItem(path);
            var result = new Dictionary<string, object>
            {
                { "path", path },
                { "executed", executed },
                { "enabled", enabled },
            };
            if (!executed)
                result["error"] = enabled
                    ? $"No menu item at '{path}' (paths are case-sensitive and use '/'), or it declined to run."
                    : $"The menu item at '{path}' is disabled in the current editor state.";
            return result;
        }
    }
}
