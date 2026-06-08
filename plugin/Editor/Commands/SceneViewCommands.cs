using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>Read the active Scene View camera state.</summary>
    public static class SceneViewCommands
    {
        [McpRoute("sceneview/info", "Active Scene View camera: pivot, rotation, size, ortho/2D mode, camera position.")]
        public static object Info(JObject args)
        {
            var view = SceneView.lastActiveSceneView;
            if (view == null) return new { error = "No active Scene View." };

            var pivot = view.pivot;
            var euler = view.rotation.eulerAngles;
            var camPos = view.camera != null ? view.camera.transform.position : Vector3.zero;

            return new Dictionary<string, object>
            {
                { "pivot", new Dictionary<string, object> { { "x", pivot.x }, { "y", pivot.y }, { "z", pivot.z } } },
                { "rotationEuler", new Dictionary<string, object> { { "x", euler.x }, { "y", euler.y }, { "z", euler.z } } },
                { "size", view.size },
                { "orthographic", view.orthographic },
                { "in2DMode", view.in2DMode },
                { "cameraPosition", new Dictionary<string, object> { { "x", camPos.x }, { "y", camPos.y }, { "z", camPos.z } } },
            };
        }
    }
}
