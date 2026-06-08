using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>
    /// Editor selection and scene-view framing. selection/get and find-by-type are read-only;
    /// selection/set and focus-scene-view change editor state (selection / scene-view camera).
    /// </summary>
    public static class SelectionCommands
    {
        [McpRoute("selection/get", "Currently selected GameObjects (paths + instance ids).")]
        public static object Get(JObject args)
        {
            var list = new List<object>();
            foreach (var go in Selection.gameObjects)
                list.Add(new Dictionary<string, object>
                {
                    { "name", go.name },
                    { "path", InspectionUtil.GetPath(go.transform) },
                    { "instanceId", go.GetInstanceID() },
                });
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

        [McpRoute("selection/set", "Set the editor selection. Args: paths (string[]) and/or instanceIds (int[]).")]
        public static object Set(JObject args)
        {
            var objects = new List<Object>();

            var paths = args["paths"] as JArray;
            if (paths is not null)
                foreach (var p in paths)
                {
                    var go = InspectionUtil.FindByPath(p.ToString());
                    if (go != null) objects.Add(go);
                }

            var ids = args["instanceIds"] as JArray;
            if (ids is not null)
                foreach (var id in ids)
                {
                    var obj = InspectionUtil.FindByInstanceId(id.Value<int>());
                    if (obj != null) objects.Add(obj);
                }

            Selection.objects = objects.ToArray();
            return new Dictionary<string, object> { { "success", true }, { "selectedCount", objects.Count } };
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
