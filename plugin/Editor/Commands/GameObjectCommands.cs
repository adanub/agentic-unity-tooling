using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>Read a single GameObject's detail: transform, components, children, parent.</summary>
    public static class GameObjectCommands
    {
        [McpRoute("gameobject/info", "GameObject detail. Args: one of path, name, instanceId. Returns transform, components, children, tag, layer.")]
        public static object Info(JObject args)
        {
            var go = InspectionUtil.ResolveGameObject(args);
            if (go == null) return new { error = "GameObject not found. Provide path, name, or instanceId." };

            var t = go.transform;
            var children = new List<object>();
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                children.Add(new Dictionary<string, object>
                {
                    { "name", c.name },
                    { "active", c.gameObject.activeSelf },
                    { "instanceId", c.gameObject.GetInstanceID() },
                });
            }

            return new Dictionary<string, object>
            {
                { "name", go.name },
                { "path", InspectionUtil.GetPath(t) },
                { "instanceId", go.GetInstanceID() },
                { "activeSelf", go.activeSelf },
                { "activeInHierarchy", go.activeInHierarchy },
                { "isStatic", go.isStatic },
                { "tag", go.tag },
                { "layer", LayerMask.LayerToName(go.layer) },
                { "parent", t.parent != null ? InspectionUtil.GetPath(t.parent) : null },
                {
                    "transform", new Dictionary<string, object>
                    {
                        { "position", V3(t.position) },
                        { "localPosition", V3(t.localPosition) },
                        { "localEulerAngles", V3(t.localEulerAngles) },
                        { "localScale", V3(t.localScale) },
                    }
                },
                { "components", InspectionUtil.SummariseComponents(go) },
                { "childCount", t.childCount },
                { "children", children },
            };
        }

        private static Dictionary<string, object> V3(Vector3 v) =>
            new Dictionary<string, object> { { "x", v.x }, { "y", v.y }, { "z", v.z } };
    }
}
