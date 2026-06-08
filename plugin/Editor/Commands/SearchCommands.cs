using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>
    /// Read-only scene/asset search routes. Scene searches return GameObject path + instance id;
    /// asset searches use the AssetDatabase. All capped by a 'limit' arg.
    /// </summary>
    public static class SearchCommands
    {
        private const int DefaultLimit = 200;

        private static object GoResult(GameObject go) => new Dictionary<string, object>
        {
            { "name", go.name },
            { "path", InspectionUtil.GetPath(go.transform) },
            { "instanceId", go.GetInstanceID() },
            { "active", go.activeInHierarchy },
        };

        [McpRoute("search/by-name", "Find GameObjects whose name matches. Args: query (required), regex (false), limit (200).")]
        public static object ByName(JObject args)
        {
            string query = args.Value<string>("query");
            if (string.IsNullOrEmpty(query)) return new { error = "Missing 'query'." };
            bool regex = args.Value<bool?>("regex") ?? false;
            int limit = args.Value<int?>("limit") ?? DefaultLimit;

            Regex re = null;
            if (regex)
            {
                try { re = new Regex(query, RegexOptions.IgnoreCase); }
                catch (Exception ex) { return new { error = $"Invalid regex: {ex.Message}" }; }
            }

            var results = new List<object>();
            foreach (var go in UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (results.Count >= limit) break;
                bool match = re != null ? re.IsMatch(go.name)
                                        : go.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                if (match) results.Add(GoResult(go));
            }
            return Wrap(results, limit);
        }

        [McpRoute("search/by-component", "Find GameObjects with a component type. Args: type (required, e.g. 'Rigidbody'), limit (200).")]
        public static object ByComponent(JObject args)
        {
            string typeName = args.Value<string>("type");
            if (string.IsNullOrEmpty(typeName)) return new { error = "Missing 'type'." };
            int limit = args.Value<int?>("limit") ?? DefaultLimit;

            var results = new List<object>();
            foreach (var go in UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (results.Count >= limit) break;
                foreach (var c in go.GetComponents<Component>())
                {
                    if (c == null) continue;
                    if (c.GetType().Name == typeName || c.GetType().FullName == typeName)
                    {
                        results.Add(GoResult(go));
                        break;
                    }
                }
            }
            return Wrap(results, limit);
        }

        [McpRoute("search/by-tag", "Find GameObjects by tag. Args: tag (required), limit (200).")]
        public static object ByTag(JObject args)
        {
            string tag = args.Value<string>("tag");
            if (string.IsNullOrEmpty(tag)) return new { error = "Missing 'tag'." };
            int limit = args.Value<int?>("limit") ?? DefaultLimit;
            try
            {
                var results = new List<object>();
                foreach (var go in GameObject.FindGameObjectsWithTag(tag))
                {
                    if (results.Count >= limit) break;
                    results.Add(GoResult(go));
                }
                return Wrap(results, limit);
            }
            catch (UnityException) { return new { error = $"Tag '{tag}' is not defined." }; }
        }

        [McpRoute("search/by-layer", "Find GameObjects on a layer. Args: layer (name or index, required), limit (200).")]
        public static object ByLayer(JObject args)
        {
            string layerName = args.Value<string>("layer");
            if (string.IsNullOrEmpty(layerName)) return new { error = "Missing 'layer'." };
            int layer = int.TryParse(layerName, out int idx) ? idx : LayerMask.NameToLayer(layerName);
            if (layer < 0) return new { error = $"Layer '{layerName}' not found." };
            int limit = args.Value<int?>("limit") ?? DefaultLimit;

            var results = new List<object>();
            foreach (var go in UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (results.Count >= limit) break;
                if (go.layer == layer) results.Add(GoResult(go));
            }
            return Wrap(results, limit);
        }

        [McpRoute("search/by-shader", "Find renderers using a shader. Args: shader (name, required), limit (200).")]
        public static object ByShader(JObject args)
        {
            string shaderName = args.Value<string>("shader");
            if (string.IsNullOrEmpty(shaderName)) return new { error = "Missing 'shader'." };
            int limit = args.Value<int?>("limit") ?? DefaultLimit;

            var results = new List<object>();
            foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (results.Count >= limit) break;
                foreach (var m in r.sharedMaterials)
                {
                    if (m != null && m.shader != null &&
                        m.shader.name.IndexOf(shaderName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var entry = (Dictionary<string, object>)GoResult(r.gameObject);
                        entry["shader"] = m.shader.name;
                        results.Add(entry);
                        break;
                    }
                }
            }
            return Wrap(results, limit);
        }

        [McpRoute("search/assets", "Search project assets. Args: filter (AssetDatabase filter, e.g. 't:Material name'), folder (optional), limit (200).")]
        public static object Assets(JObject args)
        {
            string filter = args.Value<string>("filter") ?? "";
            string folder = args.Value<string>("folder");
            int limit = args.Value<int?>("limit") ?? DefaultLimit;

            string[] guids = string.IsNullOrEmpty(folder)
                ? AssetDatabase.FindAssets(filter)
                : AssetDatabase.FindAssets(filter, new[] { folder });

            var results = new List<object>();
            for (int i = 0; i < guids.Length && results.Count < limit; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                results.Add(new Dictionary<string, object>
                {
                    { "path", path },
                    { "type", AssetDatabase.GetMainAssetTypeAtPath(path)?.Name ?? "Unknown" },
                    { "guid", guids[i] },
                });
            }
            return new Dictionary<string, object>
            {
                { "count", results.Count },
                { "total", guids.Length },
                { "truncated", guids.Length > results.Count },
                { "results", results },
            };
        }

        [McpRoute("search/missing-references", "Find missing scripts and broken object references in loaded scenes. Args: limit (200).")]
        public static object MissingReferences(JObject args)
        {
            int limit = args.Value<int?>("limit") ?? DefaultLimit;
            var results = new List<object>();

            foreach (var go in UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (results.Count >= limit) break;

                var components = go.GetComponents<Component>();
                for (int ci = 0; ci < components.Length; ci++)
                {
                    if (results.Count >= limit) break;
                    var c = components[ci];
                    if (c == null)
                    {
                        results.Add(new Dictionary<string, object>
                        {
                            { "path", InspectionUtil.GetPath(go.transform) },
                            { "issue", "missing script" },
                            { "componentIndex", ci },
                        });
                        continue;
                    }

                    using (var so = new SerializedObject(c))
                    {
                        var prop = so.GetIterator();
                        while (prop.NextVisible(true))
                        {
                            if (prop.propertyType != SerializedPropertyType.ObjectReference) continue;
                            if (prop.objectReferenceValue == null &&
                                prop.objectReferenceInstanceIDValue != 0)
                            {
                                results.Add(new Dictionary<string, object>
                                {
                                    { "path", InspectionUtil.GetPath(go.transform) },
                                    { "issue", "missing reference" },
                                    { "component", c.GetType().Name },
                                    { "property", prop.name },
                                });
                                if (results.Count >= limit) break;
                            }
                        }
                    }
                }
            }
            return Wrap(results, limit);
        }

        private static object Wrap(List<object> results, int limit) => new Dictionary<string, object>
        {
            { "count", results.Count },
            { "truncated", results.Count >= limit },
            { "results", results },
        };
    }
}
