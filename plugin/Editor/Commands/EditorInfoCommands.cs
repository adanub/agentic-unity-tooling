using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>
    /// Global editor / project / scene state readers. All return aggregate data with no
    /// target parameters, so they are safe and cheap to call at any time.
    /// </summary>
    public static class EditorInfoCommands
    {
        [McpRoute("editor/state", "Editor state: play/pause/compile/update flags, active scene, selection count.")]
        public static object GetState(JObject args)
        {
            var scene = SceneManager.GetActiveScene();
            return new Dictionary<string, object>
            {
                { "isPlaying", EditorApplication.isPlaying },
                { "isPaused", EditorApplication.isPaused },
                { "isCompiling", EditorApplication.isCompiling },
                { "isUpdating", EditorApplication.isUpdating },
                { "activeScene", scene.name },
                { "activeScenePath", scene.path },
                { "activeSceneDirty", scene.isDirty },
                { "selectionCount", Selection.objects is not null ? Selection.objects.Length : 0 },
            };
        }

        [McpRoute("project/info", "Project info: name, paths, Unity version, render pipeline, build target, scene count.")]
        public static object GetProjectInfo(JObject args)
        {
            var rp = GraphicsSettings.currentRenderPipeline;
            return new Dictionary<string, object>
            {
                { "projectName", Application.productName },
                { "companyName", Application.companyName },
                { "unityVersion", Application.unityVersion },
                { "projectPath", ProjectPath() },
                { "dataPath", Application.dataPath },
                { "platform", Application.platform.ToString() },
                { "renderPipeline", rp != null ? rp.GetType().Name : "Built-in" },
                { "activeBuildTarget", EditorUserBuildSettings.activeBuildTarget.ToString() },
                { "scenesInBuildSettings", EditorBuildSettings.scenes.Length },
            };
        }

        [McpRoute("scene/info", "Open scene(s): name, path, loaded/dirty/active state, root object count.")]
        public static object GetSceneInfo(JObject args)
        {
            var active = SceneManager.GetActiveScene();
            var scenes = new List<object>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                scenes.Add(new Dictionary<string, object>
                {
                    { "name", s.name },
                    { "path", s.path },
                    { "isLoaded", s.isLoaded },
                    { "isDirty", s.isDirty },
                    { "isActive", s == active },
                    { "rootCount", s.isLoaded ? s.rootCount : 0 },
                });
            }
            return new Dictionary<string, object> { { "sceneCount", SceneManager.sceneCount }, { "scenes", scenes } };
        }

        [McpRoute("scene/stats", "Scene totals across loaded scenes: objects, renderers, lights, cameras, colliders, verts, tris.")]
        public static object GetSceneStats(JObject args)
        {
            var transforms = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var cameras = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var colliders3D = UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var colliders2D = UnityEngine.Object.FindObjectsByType<Collider2D>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            long verts = 0, tris = 0;
            var meshFilters = UnityEngine.Object.FindObjectsByType<MeshFilter>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var mf in meshFilters)
                AccumulateMesh(mf.sharedMesh, ref verts, ref tris);
            var skinned = UnityEngine.Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var smr in skinned)
                AccumulateMesh(smr.sharedMesh, ref verts, ref tris);

            int realtimeLights = 0, bakedLights = 0, shadowLights = 0;
            foreach (var l in lights)
            {
                if (l.lightmapBakeType == LightmapBakeType.Realtime) realtimeLights++;
                else if (l.lightmapBakeType == LightmapBakeType.Baked) bakedLights++;
                if (l.shadows != LightShadows.None) shadowLights++;
            }

            return new Dictionary<string, object>
            {
                { "gameObjectCount", transforms.Length },
                { "rendererCount", renderers.Length },
                { "lightCount", lights.Length },
                { "realtimeLights", realtimeLights },
                { "bakedLights", bakedLights },
                { "shadowCastingLights", shadowLights },
                { "cameraCount", cameras.Length },
                { "collider3DCount", colliders3D.Length },
                { "collider2DCount", colliders2D.Length },
                { "approxVertices", verts },
                { "approxTriangles", tris },
            };
        }

        // Uses index counts per submesh (no managed triangles[] allocation).
        private static void AccumulateMesh(Mesh mesh, ref long verts, ref long tris)
        {
            if (mesh == null) return;
            verts += mesh.vertexCount;
            for (int s = 0; s < mesh.subMeshCount; s++)
                tris += mesh.GetIndexCount(s) / 3;
        }

        private static string ProjectPath()
        {
            string dataPath = Application.dataPath;
            return dataPath.EndsWith("/Assets")
                ? dataPath.Substring(0, dataPath.Length - "/Assets".Length)
                : dataPath;
        }
    }
}
