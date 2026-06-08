using System.Diagnostics;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>
    /// Health-check route. Confirms the bridge is alive and returns basic editor identity —
    /// also the smallest possible end-to-end test of the Unity ⟷ HTTP ⟷ Node ⟷ MCP pipeline.
    /// </summary>
    public static class PingCommand
    {
        [McpRoute("ping", "Check the bridge is alive; returns Unity version, project name, and path.")]
        public static object Ping(JObject args)
        {
            string dataPath = Application.dataPath; // ".../<Project>/Assets"
            string projectPath = dataPath.EndsWith("/Assets")
                ? dataPath.Substring(0, dataPath.Length - "/Assets".Length)
                : dataPath;

            return new
            {
                status = "ok",
                unityVersion = Application.unityVersion,
                projectName = Application.productName,
                projectPath,
                port = McpBridgeServer.ActivePort,
                isPlaying = EditorApplication.isPlaying,
                isCompiling = EditorApplication.isCompiling,
                processId = Process.GetCurrentProcess().Id,
            };
        }
    }
}
