using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
// This file works in UnityEngine.Object throughout; the alias keeps bare `Object` unambiguous
// now that System is imported.
using Object = UnityEngine.Object;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>
    /// Editor selection and scene-view framing. selection/get and find-by-type are read-only;
    /// selection/set and focus-scene-view change editor state (selection / scene-view camera).
    /// </summary>
    public static class SelectionCommands
    {
        [McpRoute("selection/get", "Currently selected objects — scene GameObjects (with hierarchy paths) and project assets (with asset paths).")]
        public static object Get(JObject args)
        {
            var list = new List<object>();
            // Selection.objects rather than Selection.gameObjects: an asset selected in the Project
            // window is not a GameObject, so the narrower call reports an empty selection for it and
            // makes a successful selection look like a failed one.
            foreach (var obj in Selection.objects)
            {
                if (obj == null) continue;
                var entry = new Dictionary<string, object>
                {
                    { "name", obj.name },
                    { "type", obj.GetType().Name },
                    { "instanceId", obj.GetInstanceID() },
                };
                var assetPath = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrEmpty(assetPath)) entry["assetPath"] = assetPath;
                else if (obj is GameObject go) entry["path"] = InspectionUtil.GetPath(go.transform);
                list.Add(entry);
            }

            return new Dictionary<string, object> { { "count", list.Count }, { "selection", list } };
        }

        [McpRoute("selection/find-by-type", "Find GameObjects with a component type. Args: type (required), limit (200).")]
        public static object FindByType(JObject args)
        {
            string typeName = args.Value<string>("type");
            if (string.IsNullOrEmpty(typeName)) return new { error = "Missing 'type'." };
            int limit = args.Value<int?>("limit") ?? 200;

            var results = new List<object>();
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (results.Count >= limit) break;
                foreach (var c in go.GetComponents<Component>())
                {
                    if (c != null && (c.GetType().Name == typeName || c.GetType().FullName == typeName))
                    {
                        results.Add(new Dictionary<string, object>
                        {
                            { "name", go.name },
                            { "path", InspectionUtil.GetPath(go.transform) },
                            { "instanceId", go.GetInstanceID() },
                        });
                        break;
                    }
                }
            }
            return new Dictionary<string, object> { { "count", results.Count }, { "truncated", results.Count >= limit }, { "results", results } };
        }

        [McpRoute("selection/set",
            "Set the editor selection to scene objects and/or project assets. Args: paths (string[] scene paths), instanceIds (int[]), assetPaths (string[] e.g. 'Assets/Foo.asset'), guids (string[]), ping (bool — also highlight the first in the Project window). Reports anything that did not resolve, and warns if an Inspector is locked.")]
        public static object Set(JObject args)
        {
            // Guarded on ARRAY-ness, not key presence: every branch below reads `as JArray`, so a
            // key holding a bare string or a JSON null would sail past a presence check and then
            // silently clear the selection while reporting success — the exact "selected nothing" /
            // "selected the wrong thing" ambiguity this route exists to remove. An empty array is a
            // JArray, so clearing deliberately still works.
            string[] selectorKeys = { "paths", "instanceIds", "assetPaths", "guids" };
            if (selectorKeys.All(k => args[k] is not JArray))
                return new
                {
                    error = "No selection specified. Pass paths, instanceIds, assetPaths or guids as ARRAYS; "
                            + "pass an empty array to clear the selection deliberately.",
                    received = selectorKeys.Where(k => args[k] is not null)
                        .Select(k => $"{k}:{args[k].Type}").ToArray(),
                };

            var objects = new List<Object>();
            // Every input that resolved to nothing is named back. "Selected 0" and "selected the
            // wrong thing" are indistinguishable to a caller that then inspects the selection, and
            // a silently empty selection makes whatever is inspected next look like a finding.
            var unresolved = new List<string>();

            var paths = args["paths"] as JArray;
            if (paths is not null)
                foreach (var p in paths)
                {
                    var go = InspectionUtil.FindByPath(p.ToString());
                    if (go != null) objects.Add(go);
                    else unresolved.Add($"path:{p}");
                }

            var ids = args["instanceIds"] as JArray;
            if (ids is not null)
                foreach (var id in ids)
                {
                    var obj = InspectionUtil.FindByInstanceId(id.Value<int>());
                    if (obj != null) objects.Add(obj);
                    else unresolved.Add($"instanceId:{id}");
                }

            var assetPaths = args["assetPaths"] as JArray;
            if (assetPaths is not null)
                foreach (var p in assetPaths)
                {
                    var asset = AssetDatabase.LoadAssetAtPath<Object>(p.ToString());
                    if (asset != null) objects.Add(asset);
                    else unresolved.Add($"assetPath:{p}");
                }

            var guids = args["guids"] as JArray;
            if (guids is not null)
                foreach (var g in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(g.ToString());
                    var asset = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<Object>(path);
                    if (asset != null) objects.Add(asset);
                    else unresolved.Add($"guid:{g}");
                }

            Selection.objects = objects.ToArray();
            if (objects.Count > 0 && args.Value<bool?>("ping") == true)
                EditorGUIUtility.PingObject(objects[0]);

            // Setting the selection is not enough for the Inspector to show it. The window rebuilds
            // its editor list from the active tracker when it next handles a selection change, and
            // an editor running unfocused may not get there — leaving the Inspector displaying the
            // PREVIOUS object while the selection genuinely is the new one. Rebuilding the tracker
            // here is what a real click does, and without it a following dump measures the wrong
            // object while every other signal says the selection succeeded.
            var trackerRebuilt = false;
            try
            {
                ActiveEditorTracker.sharedTracker.ForceRebuild();
                trackerRebuilt = true;
            }
            catch (Exception)
            {
                // Internal-API drift must not fail the selection itself; the caller is told.
            }

            var result = new Dictionary<string, object>
            {
                { "success", unresolved.Count == 0 },
                { "selectedCount", objects.Count },
                { "selected", objects.Select(o => o.name).ToArray() },
                { "inspectorTrackerRebuilt", trackerRebuilt },
            };
            if (unresolved.Count > 0) result["unresolved"] = unresolved.ToArray();
            // A locked inspector keeps showing its pinned object, so a caller that selects an asset
            // and then dumps the inspector would measure something else entirely.
            var pinned = PinnedInspectors();
            if (pinned.Length > 0)
                result["warning"] = $"These inspector window(s) will NOT follow this selection: {string.Join(", ", pinned)}.";
            return result;
        }

        /// <summary>
        /// Inspector windows that will keep showing their current object regardless of the
        /// selection: locked docked inspectors, and floating property editors, which are pinned to
        /// one object by their nature and expose no lock flag at all. Dumping either after a
        /// selection change measures a different object than the caller believes it selected.
        /// </summary>
        private static string[] PinnedInspectors()
        {
            var pinned = new List<string>();
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window == null) continue;
                var name = window.GetType().Name;
                // Exactly "PropertyEditor", not a subclass test: InspectorWindow derives from it,
                // and a docked Inspector is not pinned. The floating Properties window IS this
                // concrete type and is pinned to its object by construction.
                if (name == "PropertyEditor")
                    pinned.Add("PropertyEditor (floating, always pinned)");
                // Restricted to inspectors before consulting the lock flag: other windows declare
                // an `isLocked` of their own (ProjectBrowser does), and a locked Project window
                // does not stop the Inspector following the selection.
                // One owner for the lock policy — the dump reports the same flag.
                else if (name == "InspectorWindow" && UiToolkitCommands.IsLocked(window) == true)
                    pinned.Add($"{name} (locked)");
            }

            return pinned.ToArray();
        }

        [McpRoute("selection/focus-scene-view", "Frame the scene-view camera on a GameObject (path/name/instanceId) or the current selection.")]
        public static object FocusSceneView(JObject args)
        {
            var go = InspectionUtil.ResolveGameObject(args);
            if (go != null) Selection.activeGameObject = go;

            var view = SceneView.lastActiveSceneView;
            if (view == null) return new { error = "No active Scene View to frame." };
            view.FrameSelected();
            return new Dictionary<string, object>
            {
                { "success", true },
                { "framed", go != null ? go.name : (Selection.activeGameObject != null ? Selection.activeGameObject.name : "(selection)") },
            };
        }
    }
}
