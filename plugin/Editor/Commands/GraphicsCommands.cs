using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>
    /// Read-only graphics inspectors. Each resolves its target from `assetPath` (an asset) or
    /// a GameObject identifier (path/name/instanceId), as appropriate.
    /// </summary>
    public static class GraphicsCommands
    {
        private static Dictionary<string, object> V3(Vector3 v) =>
            new Dictionary<string, object> { { "x", v.x }, { "y", v.y }, { "z", v.z } };

        private static Dictionary<string, object> BoundsObj(Bounds b) =>
            new Dictionary<string, object> { { "center", V3(b.center) }, { "size", V3(b.size) } };

        [McpRoute("graphics/mesh-info", "Mesh stats. Args: assetPath (mesh asset) OR a GameObject (path/name/instanceId) with a MeshFilter/SkinnedMeshRenderer.")]
        public static object MeshInfo(JObject args)
        {
            Mesh mesh = null;
            string source = null;

            string assetPath = args.Value<string>("assetPath");
            if (!string.IsNullOrEmpty(assetPath)) { mesh = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath); if (mesh != null) source = "asset:" + assetPath; }

            if (mesh == null)
            {
                var go = InspectionUtil.ResolveGameObject(args);
                if (go != null)
                {
                    var mf = go.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null) { mesh = mf.sharedMesh; source = "gameObject:" + go.name; }
                    if (mesh == null)
                    {
                        var smr = go.GetComponent<SkinnedMeshRenderer>();
                        if (smr != null && smr.sharedMesh != null) { mesh = smr.sharedMesh; source = "gameObject:" + go.name; }
                    }
                }
            }

            if (mesh == null) return new { error = "No mesh found. Provide a mesh 'assetPath' or a GameObject with a MeshFilter/SkinnedMeshRenderer." };

            long tris = 0;
            for (int s = 0; s < mesh.subMeshCount; s++) tris += mesh.GetIndexCount(s) / 3;

            return new Dictionary<string, object>
            {
                { "source", source },
                { "name", mesh.name },
                { "vertices", mesh.vertexCount },
                { "triangles", tris },
                { "subMeshCount", mesh.subMeshCount },
                { "isReadable", mesh.isReadable },
                { "bounds", BoundsObj(mesh.bounds) },
            };
        }

        [McpRoute("graphics/material-info", "Material details. Args: assetPath (material asset) OR a GameObject (renderer's first material).")]
        public static object MaterialInfo(JObject args)
        {
            Material mat = null;
            string source = null;

            string assetPath = args.Value<string>("assetPath");
            if (!string.IsNullOrEmpty(assetPath)) { mat = AssetDatabase.LoadAssetAtPath<Material>(assetPath); if (mat != null) source = "asset:" + assetPath; }

            if (mat == null)
            {
                var go = InspectionUtil.ResolveGameObject(args);
                var r = go != null ? go.GetComponent<Renderer>() : null;
                if (r != null && r.sharedMaterial != null) { mat = r.sharedMaterial; source = "gameObject:" + go.name; }
            }

            if (mat == null) return new { error = "No material found. Provide a material 'assetPath' or a GameObject with a Renderer." };

            return new Dictionary<string, object>
            {
                { "source", source },
                { "name", mat.name },
                { "shader", mat.shader != null ? mat.shader.name : null },
                { "renderQueue", mat.renderQueue },
                { "enabledKeywords", mat.shaderKeywords },
                { "mainTexture", mat.mainTexture != null ? mat.mainTexture.name : null },
            };
        }

        [McpRoute("graphics/texture-info", "Texture runtime details. Args: assetPath (texture asset).")]
        public static object TextureInfo(JObject args)
        {
            string assetPath = args.Value<string>("assetPath");
            if (string.IsNullOrEmpty(assetPath)) return new { error = "Missing 'assetPath'." };

            var tex = AssetDatabase.LoadAssetAtPath<Texture>(assetPath);
            if (tex == null) return new { error = $"No texture at '{assetPath}'." };

            var result = new Dictionary<string, object>
            {
                { "name", tex.name },
                { "type", tex.GetType().Name },
                { "width", tex.width },
                { "height", tex.height },
                { "dimension", tex.dimension.ToString() },
                { "filterMode", tex.filterMode.ToString() },
                { "wrapMode", tex.wrapMode.ToString() },
                { "anisoLevel", tex.anisoLevel },
                { "graphicsFormat", tex.graphicsFormat.ToString() },
            };
            if (tex is Texture2D t2d)
            {
                result["format"] = t2d.format.ToString();
                result["mipmapCount"] = t2d.mipmapCount;
            }
            return result;
        }

        [McpRoute("graphics/renderer-info", "Renderer details. Args: a GameObject (path/name/instanceId).")]
        public static object RendererInfo(JObject args)
        {
            var go = InspectionUtil.ResolveGameObject(args);
            if (go == null) return new { error = "GameObject not found. Provide path, name, or instanceId." };

            var r = go.GetComponent<Renderer>();
            if (r == null) return new { error = $"'{go.name}' has no Renderer." };

            var materials = new List<object>();
            foreach (var m in r.sharedMaterials)
                materials.Add(new Dictionary<string, object>
                {
                    { "name", m != null ? m.name : null },
                    { "shader", m != null && m.shader != null ? m.shader.name : null },
                });

            return new Dictionary<string, object>
            {
                { "gameObject", InspectionUtil.GetPath(go.transform) },
                { "rendererType", r.GetType().Name },
                { "enabled", r.enabled },
                { "materialCount", r.sharedMaterials.Length },
                { "materials", materials },
                { "sortingLayer", r.sortingLayerName },
                { "sortingOrder", r.sortingOrder },
                { "shadowCasting", r.shadowCastingMode.ToString() },
                { "receiveShadows", r.receiveShadows },
                { "bounds", BoundsObj(r.bounds) },
            };
        }

        [McpRoute("graphics/lighting-summary", "Scene lighting overview: ambient, fog, skybox, and light counts by type.")]
        public static object LightingSummary(JObject args)
        {
            var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            int directional = 0, point = 0, spot = 0, area = 0;
            foreach (var l in lights)
            {
                switch (l.type)
                {
                    case LightType.Directional: directional++; break;
                    case LightType.Point: point++; break;
                    case LightType.Spot: spot++; break;
                    default: area++; break;
                }
            }

            var amb = RenderSettings.ambientLight;
            return new Dictionary<string, object>
            {
                { "ambientMode", RenderSettings.ambientMode.ToString() },
                { "ambientColor", new Dictionary<string, object> { { "r", amb.r }, { "g", amb.g }, { "b", amb.b } } },
                { "ambientIntensity", RenderSettings.ambientIntensity },
                { "fogEnabled", RenderSettings.fog },
                { "fogMode", RenderSettings.fogMode.ToString() },
                { "fogDensity", RenderSettings.fogDensity },
                { "skybox", RenderSettings.skybox != null ? RenderSettings.skybox.name : null },
                { "sun", RenderSettings.sun != null ? RenderSettings.sun.name : null },
                {
                    "lights", new Dictionary<string, object>
                    {
                        { "total", lights.Length },
                        { "directional", directional },
                        { "point", point },
                        { "spot", spot },
                        { "areaOrOther", area },
                    }
                },
            };
        }
    }
}
