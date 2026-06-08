using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine.U2D;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>List sprite atlases and read their packables/settings.</summary>
    public static class SpriteAtlasCommands
    {
        [McpRoute("spriteatlas/list", "List SpriteAtlas assets. Args: limit (200).")]
        public static object List(JObject args)
        {
            int limit = args.Value<int?>("limit") ?? 200;
            var result = new List<object>();
            foreach (var guid in AssetDatabase.FindAssets("t:SpriteAtlas"))
            {
                if (result.Count >= limit) break;
                string path = AssetDatabase.GUIDToAssetPath(guid);
                result.Add(new Dictionary<string, object>
                {
                    { "path", path },
                    { "name", System.IO.Path.GetFileNameWithoutExtension(path) },
                });
            }
            return new Dictionary<string, object> { { "count", result.Count }, { "atlases", result } };
        }

        [McpRoute("spriteatlas/info", "SpriteAtlas details: sprite count, variant flag, packables. Args: path.")]
        public static object Info(JObject args)
        {
            string path = args.Value<string>("path") ?? args.Value<string>("assetPath");
            if (string.IsNullOrEmpty(path)) return new { error = "Missing 'path'." };

            var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
            if (atlas == null) return new { error = $"No SpriteAtlas at '{path}'." };

            var packables = SpriteAtlasExtensions.GetPackables(atlas);
            var packableList = new List<object>();
            foreach (var p in packables)
            {
                if (p == null) continue;
                packableList.Add(new Dictionary<string, object>
                {
                    { "name", p.name },
                    { "type", p.GetType().Name },
                    { "path", AssetDatabase.GetAssetPath(p) },
                });
            }

            return new Dictionary<string, object>
            {
                { "path", path },
                { "name", atlas.name },
                { "spriteCount", atlas.spriteCount },
                { "isVariant", atlas.isVariant },
                { "packableCount", packables.Length },
                { "packables", packableList },
            };
        }
    }
}
