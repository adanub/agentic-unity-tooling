using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Profiler = UnityEngine.Profiling.Profiler;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>
    /// Asset memory inspection using built-in Profiler APIs (no Memory Profiler package
    /// required). Reports per-type breakdowns and the largest individual assets in memory.
    /// </summary>
    public static class MemoryCommands
    {
        private const double BytesPerMB = 1024.0 * 1024.0;
        private static double Mb(long bytes) => Math.Round(bytes / BytesPerMB, 2);

        // Type buckets for the breakdown. Note: RenderTexture is also a Texture, so it is
        // reported both under "renderTextures" and within "textures" — surfaced in the note.
        private static readonly (string Name, Type Type)[] Buckets =
        {
            ("textures", typeof(Texture)),
            ("meshes", typeof(Mesh)),
            ("materials", typeof(Material)),
            ("shaders", typeof(Shader)),
            ("audioClips", typeof(AudioClip)),
            ("animationClips", typeof(AnimationClip)),
            ("fonts", typeof(Font)),
            ("renderTextures", typeof(RenderTexture)),
            ("scriptableObjects", typeof(ScriptableObject)),
        };

        [McpRoute("memory/status", "Memory summary + whether the com.unity.memoryprofiler package is installed.")]
        public static object Status(JObject args)
        {
            bool packageInstalled = AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => a.GetName().Name == "Unity.MemoryProfiler.Editor");

            return new Dictionary<string, object>
            {
                { "memoryProfilerPackageInstalled", packageInstalled },
                { "totalAllocatedMB", Mb(Profiler.GetTotalAllocatedMemoryLong()) },
                { "totalReservedMB", Mb(Profiler.GetTotalReservedMemoryLong()) },
                { "monoUsedMB", Mb(Profiler.GetMonoUsedSizeLong()) },
                { "gfxDriverMB", Mb(Profiler.GetAllocatedMemoryForGraphicsDriver()) },
                {
                    "note",
                    packageInstalled
                        ? "Memory Profiler package present; breakdown/top-assets use built-in APIs (fast, no full snapshot)."
                        : "Memory Profiler package not installed; breakdown/top-assets use built-in Profiler APIs (no full snapshot)."
                },
            };
        }

        [McpRoute("memory/breakdown", "Memory by asset type (textures, meshes, materials, shaders, audio, animation, fonts, render textures, SOs). Count + size.")]
        public static object Breakdown(JObject args)
        {
            var result = new Dictionary<string, object>();
            foreach (var (name, type) in Buckets)
            {
                var objects = Resources.FindObjectsOfTypeAll(type);
                long total = 0;
                foreach (var o in objects) total += Profiler.GetRuntimeMemorySizeLong(o);
                result[name] = new Dictionary<string, object>
                {
                    { "count", objects.Length },
                    { "totalMB", Mb(total) },
                    { "totalBytes", total },
                };
            }
            result["note"] = "RenderTexture instances are also counted within 'textures'.";
            return result;
        }

        [McpRoute("memory/top-assets", "Largest individual assets in memory. Args: limit (default 25), type (texture|mesh|material|shader|audio|animation|font|rendertexture).")]
        public static object TopAssets(JObject args)
        {
            int limit = Math.Max(1, args.Value<int?>("limit") ?? 25);
            Type filter = ResolveType(args.Value<string>("type"));

            var objects = Resources.FindObjectsOfTypeAll(filter ?? typeof(UnityEngine.Object));

            var top = objects
                .Select(o => new { Obj = o, Size = Profiler.GetRuntimeMemorySizeLong(o) })
                .Where(x => x.Size > 0)
                .OrderByDescending(x => x.Size)
                .Take(limit)
                .Select(x => new Dictionary<string, object>
                {
                    { "name", x.Obj.name },
                    { "type", x.Obj.GetType().Name },
                    { "sizeMB", Math.Round(x.Size / BytesPerMB, 3) },
                    { "sizeBytes", x.Size },
                    { "assetPath", AssetDatabase.GetAssetPath(x.Obj) },
                })
                .ToList();

            return new Dictionary<string, object> { { "count", top.Count }, { "assets", top } };
        }

        private static Type ResolveType(string filter)
        {
            if (string.IsNullOrEmpty(filter)) return null;
            switch (filter.ToLowerInvariant())
            {
                case "texture": return typeof(Texture);
                case "mesh": return typeof(Mesh);
                case "material": return typeof(Material);
                case "shader": return typeof(Shader);
                case "audio":
                case "audioclip": return typeof(AudioClip);
                case "animation":
                case "animationclip": return typeof(AnimationClip);
                case "font": return typeof(Font);
                case "rendertexture": return typeof(RenderTexture);
                default: return null;
            }
        }
    }
}
