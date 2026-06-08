using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>Project asset listing and C# script reading (read-only).</summary>
    public static class AssetCommands
    {
        [McpRoute("asset/list", "List project assets. Args: folder (e.g. 'Assets/Art'), type (e.g. 'Material'), term (name filter), limit (200).")]
        public static object List(JObject args)
        {
            string folder = args.Value<string>("folder");
            string type = args.Value<string>("type");
            string term = args.Value<string>("term") ?? "";
            int limit = args.Value<int?>("limit") ?? 200;

            string filter = term;
            if (!string.IsNullOrEmpty(type)) filter += $" t:{type}";

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

        [McpRoute("script/read", "Read a C# (or text) asset's contents. Args: path (Assets/... relative). Capped at maxChars (default 60000).")]
        public static object Read(JObject args)
        {
            string path = args.Value<string>("path");
            if (string.IsNullOrEmpty(path)) return new { error = "Missing 'path'." };
            int maxChars = args.Value<int?>("maxChars") ?? 60000;

            // Resolve relative to the project root (parent of Assets/).
            string projectRoot = Path.GetDirectoryName(UnityEngine.Application.dataPath);
            string full = Path.IsPathRooted(path) ? path : Path.Combine(projectRoot, path);

            if (!File.Exists(full)) return new { error = $"File not found: {path}" };

            string text = File.ReadAllText(full);
            bool truncated = text.Length > maxChars;
            if (truncated) text = text.Substring(0, maxChars);

            return new Dictionary<string, object>
            {
                { "path", path },
                { "truncated", truncated },
                { "length", text.Length },
                { "content", text },
            };
        }
    }
}
