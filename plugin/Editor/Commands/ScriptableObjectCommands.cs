using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>Read ScriptableObject asset properties and enumerate available SO types.</summary>
    public static class ScriptableObjectCommands
    {
        [McpRoute("scriptableobject/info", "Serialized properties of a ScriptableObject asset. Args: path (Assets/...).")]
        public static object Info(JObject args)
        {
            string path = args.Value<string>("path");
            if (string.IsNullOrEmpty(path)) return new { error = "Missing 'path'." };

            var so = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
            if (so == null) return new { error = $"No ScriptableObject found at '{path}'." };

            return new Dictionary<string, object>
            {
                { "path", path },
                { "type", so.GetType().Name },
                { "properties", InspectionUtil.SerialiseUnityObject(so) },
            };
        }

        [McpRoute("scriptableobject/list-types", "List non-abstract ScriptableObject types defined in the project. Args: term (name filter), limit (300).")]
        public static object ListTypes(JObject args)
        {
            string term = args.Value<string>("term");
            int limit = args.Value<int?>("limit") ?? 300;

            var results = new List<object>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }

                foreach (var t in types)
                {
                    if (results.Count >= limit) break;
                    if (t.IsAbstract || !typeof(ScriptableObject).IsAssignableFrom(t)) continue;
                    // Skip editor/Unity-internal SOs to keep the list project-relevant.
                    if (typeof(UnityEditor.Editor).IsAssignableFrom(t) || typeof(EditorWindow).IsAssignableFrom(t)) continue;
                    if (!string.IsNullOrEmpty(term) && t.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    results.Add(new Dictionary<string, object>
                    {
                        { "name", t.Name },
                        { "fullName", t.FullName },
                        { "namespace", t.Namespace ?? "" },
                    });
                }
            }

            return new Dictionary<string, object>
            {
                { "count", results.Count },
                { "truncated", results.Count >= limit },
                { "types", results },
            };
        }
    }
}
