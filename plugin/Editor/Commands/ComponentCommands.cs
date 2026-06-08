using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>Read component properties, and discover what can be assigned to a reference field.</summary>
    public static class ComponentCommands
    {
        [McpRoute("component/get-properties", "Serialized properties of a component on a GameObject. Args: (path|name|instanceId) + type (component type name). Omit type to list available components.")]
        public static object GetProperties(JObject args)
        {
            var go = InspectionUtil.ResolveGameObject(args);
            if (go == null) return new { error = "GameObject not found. Provide path, name, or instanceId." };

            string typeName = args.Value<string>("type");
            if (string.IsNullOrEmpty(typeName))
                return new Dictionary<string, object>
                {
                    { "error", "Missing 'type'. Available components below." },
                    { "availableComponents", InspectionUtil.SummariseComponents(go) },
                };

            Component target = null;
            foreach (var c in go.GetComponents<Component>())
            {
                if (c != null && (c.GetType().Name == typeName || c.GetType().FullName == typeName))
                {
                    target = c;
                    break;
                }
            }
            if (target == null)
                return new Dictionary<string, object>
                {
                    { "error", $"Component '{typeName}' not found on '{go.name}'." },
                    { "availableComponents", InspectionUtil.SummariseComponents(go) },
                };

            return new Dictionary<string, object>
            {
                { "gameObject", InspectionUtil.GetPath(go.transform) },
                { "component", target.GetType().Name },
                { "properties", InspectionUtil.SerializeComponent(target) },
            };
        }

        [McpRoute("component/get-referenceable", "Scene objects and project assets assignable to a type. Args: type (required, e.g. 'Material', 'Rigidbody'), limit (100).")]
        public static object GetReferenceable(JObject args)
        {
            string typeName = args.Value<string>("type");
            if (string.IsNullOrEmpty(typeName)) return new { error = "Missing 'type'." };
            int limit = args.Value<int?>("limit") ?? 100;

            Type type = InspectionUtil.FindType(typeName);
            if (type == null) return new { error = $"Type '{typeName}' could not be resolved." };

            // Scene objects: GameObjects (if asking for GameObject) or holders of a component type.
            var sceneObjects = new List<object>();
            bool wantsGameObject = type == typeof(GameObject);
            bool wantsComponent = typeof(Component).IsAssignableFrom(type);
            if (wantsGameObject || wantsComponent)
            {
                foreach (var go in UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (sceneObjects.Count >= limit) break;
                    if (wantsGameObject || go.GetComponent(type) != null)
                        sceneObjects.Add(new Dictionary<string, object>
                        {
                            { "name", go.name },
                            { "path", InspectionUtil.GetPath(go.transform) },
                            { "instanceId", go.GetInstanceID() },
                        });
                }
            }

            // Project assets of the type.
            var assets = new List<object>();
            foreach (var guid in AssetDatabase.FindAssets($"t:{type.Name}"))
            {
                if (assets.Count >= limit) break;
                string path = AssetDatabase.GUIDToAssetPath(guid);
                assets.Add(new Dictionary<string, object> { { "path", path }, { "name", System.IO.Path.GetFileNameWithoutExtension(path) } });
            }

            return new Dictionary<string, object>
            {
                { "type", type.Name },
                { "sceneObjectCount", sceneObjects.Count },
                { "sceneObjects", sceneObjects },
                { "assetCount", assets.Count },
                { "assets", assets },
            };
        }
    }
}
