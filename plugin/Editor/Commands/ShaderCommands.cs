using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>List shaders and read their exposed properties (.shader and .shadergraph).</summary>
    public static class ShaderCommands
    {
        [McpRoute("shader/list", "List shader assets (.shader + .shadergraph). Args: term (name filter), limit (300).")]
        public static object List(JObject args)
        {
            string term = args.Value<string>("term");
            int limit = args.Value<int?>("limit") ?? 300;

            var results = new List<object>();
            foreach (var guid in AssetDatabase.FindAssets("t:Shader"))
            {
                if (results.Count >= limit) break;
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                string name = shader != null ? shader.name : System.IO.Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrEmpty(term) && name.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0) continue;
                results.Add(new Dictionary<string, object> { { "name", name }, { "path", path } });
            }

            return new Dictionary<string, object>
            {
                { "count", results.Count },
                { "truncated", results.Count >= limit },
                { "shaders", results },
            };
        }

        [McpRoute("shader/get-properties", "Exposed properties of a shader. Args: one of path (asset) or name (shader name).")]
        public static object GetProperties(JObject args)
        {
            string path = args.Value<string>("path");
            string name = args.Value<string>("name");

            Shader shader = null;
            if (!string.IsNullOrEmpty(path)) shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader == null && !string.IsNullOrEmpty(name)) shader = Shader.Find(name);
            if (shader == null) return new { error = "Shader not found. Provide a valid 'path' or 'name'." };

            // Unity 6.3: use the Shader instance API (ShaderUtil.GetProperty* are obsolete).
            int count = shader.GetPropertyCount();
            var props = new List<object>();
            for (int i = 0; i < count; i++)
            {
                ShaderPropertyType type = shader.GetPropertyType(i);
                var entry = new Dictionary<string, object>
                {
                    { "name", shader.GetPropertyName(i) },
                    { "description", shader.GetPropertyDescription(i) },
                    { "type", type.ToString() },
                    { "hidden", (shader.GetPropertyFlags(i) & ShaderPropertyFlags.HideInInspector) != 0 },
                };
                if (type == ShaderPropertyType.Range)
                {
                    Vector2 limits = shader.GetPropertyRangeLimits(i);
                    entry["rangeMin"] = limits.x;
                    entry["rangeMax"] = limits.y;
                }
                props.Add(entry);
            }

            return new Dictionary<string, object>
            {
                { "name", shader.name },
                { "propertyCount", count },
                { "properties", props },
            };
        }
    }
}
