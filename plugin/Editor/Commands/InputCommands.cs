using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>
    /// Read an Input Action Asset (.inputactions). Parses the asset's JSON directly, so it works
    /// without referencing the Input System assembly (no package dependency).
    /// </summary>
    public static class InputCommands
    {
        [McpRoute("input/info", "Input Action Asset summary (maps, actions, bindings, control schemes). Args: path (.inputactions asset).")]
        public static object Info(JObject args)
        {
            string path = args.Value<string>("path") ?? args.Value<string>("assetPath");
            if (string.IsNullOrEmpty(path)) return new { error = "Missing 'path' (.inputactions asset)." };

            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string full = Path.IsPathRooted(path) ? path : Path.Combine(projectRoot, path);
            if (!File.Exists(full)) return new { error = $"File not found: {path}" };

            JObject root;
            try { root = JObject.Parse(File.ReadAllText(full)); }
            catch (System.Exception ex) { return new { error = $"Failed to parse .inputactions JSON: {ex.Message}" }; }

            var maps = new List<object>();
            if (root["maps"] is JArray mapsArr)
            {
                foreach (var m in mapsArr)
                {
                    var actions = m["actions"] as JArray;
                    var bindings = m["bindings"] as JArray;
                    var actionList = new List<object>();
                    if (actions is not null)
                        foreach (var a in actions)
                            actionList.Add(new Dictionary<string, object>
                            {
                                { "name", a["name"]?.ToString() },
                                { "type", a["type"]?.ToString() },
                            });

                    maps.Add(new Dictionary<string, object>
                    {
                        { "name", m["name"]?.ToString() },
                        { "actionCount", actions?.Count ?? 0 },
                        { "bindingCount", bindings?.Count ?? 0 },
                        { "actions", actionList },
                    });
                }
            }

            var schemes = new List<object>();
            if (root["controlSchemes"] is JArray schemesArr)
                foreach (var s in schemesArr)
                    schemes.Add(s["name"]?.ToString());

            return new Dictionary<string, object>
            {
                { "path", path },
                { "name", root["name"]?.ToString() },
                { "mapCount", maps.Count },
                { "maps", maps },
                { "controlSchemes", schemes },
            };
        }
    }
}
