using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>Read a texture's import settings (TextureImporter) — distinct from runtime details in graphics/texture-info.</summary>
    public static class TextureImportCommands
    {
        [McpRoute("texture/info", "Texture import settings (type, compression, max size, sprite mode, filter/wrap, mipmaps, sRGB). Args: path (texture asset).")]
        public static object Info(JObject args)
        {
            string path = args.Value<string>("path") ?? args.Value<string>("assetPath");
            if (string.IsNullOrEmpty(path)) return new { error = "Missing 'path'." };

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return new { error = $"No TextureImporter at '{path}' (not a texture asset?)." };

            var platform = importer.GetDefaultPlatformTextureSettings();

            return new Dictionary<string, object>
            {
                { "path", path },
                { "textureType", importer.textureType.ToString() },
                { "shape", importer.textureShape.ToString() },
                { "sRGBTexture", importer.sRGBTexture },
                { "alphaSource", importer.alphaSource.ToString() },
                { "alphaIsTransparency", importer.alphaIsTransparency },
                { "isReadable", importer.isReadable },
                { "mipmapEnabled", importer.mipmapEnabled },
                { "wrapMode", importer.wrapMode.ToString() },
                { "filterMode", importer.filterMode.ToString() },
                { "anisoLevel", importer.anisoLevel },
                { "npotScale", importer.npotScale.ToString() },
                { "maxTextureSize", platform.maxTextureSize },
                { "textureCompression", platform.textureCompression.ToString() },
                { "format", platform.format.ToString() },
                { "crunchedCompression", platform.crunchedCompression },
                { "spriteImportMode", importer.spriteImportMode.ToString() },
                { "spritePixelsPerUnit", importer.spritePixelsPerUnit },
            };
        }
    }
}
