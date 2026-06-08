using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>
    /// Scene hierarchy dump, bounded by depth and node count so large scenes don't blow
    /// the response limit. Optionally rooted at a subtree and/or include component names.
    /// </summary>
    public static class SceneHierarchyCommands
    {
        [McpRoute("scene/hierarchy", "GameObject tree of loaded scenes. Args: maxDepth (8), maxNodes (2000), parentPath (subtree), includeComponents (false), includeInactive (true).")]
        public static object Hierarchy(JObject args)
        {
            int maxDepth = args.Value<int?>("maxDepth") ?? 8;
            int maxNodes = args.Value<int?>("maxNodes") ?? 2000;
            string parentPath = args.Value<string>("parentPath");
            bool includeComponents = args.Value<bool?>("includeComponents") ?? false;
            bool includeInactive = args.Value<bool?>("includeInactive") ?? true;

            var budget = new Budget { Remaining = maxNodes };
            var roots = new List<object>();

            if (!string.IsNullOrEmpty(parentPath))
            {
                var go = InspectionUtil.FindByPath(parentPath);
                if (go == null) return new { error = $"GameObject not found at path '{parentPath}'." };
                roots.Add(BuildNode(go.transform, 0, maxDepth, includeComponents, includeInactive, budget));
            }
            else
            {
                for (int s = 0; s < SceneManager.sceneCount && budget.Remaining > 0; s++)
                {
                    var scene = SceneManager.GetSceneAt(s);
                    if (!scene.isLoaded) continue;
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        if (budget.Remaining <= 0) break;
                        if (!includeInactive && !root.activeInHierarchy) continue;
                        roots.Add(BuildNode(root.transform, 0, maxDepth, includeComponents, includeInactive, budget));
                    }
                }
            }

            return new Dictionary<string, object>
            {
                { "nodeCount", maxNodes - budget.Remaining },
                { "truncated", budget.Truncated },
                { "roots", roots },
            };
        }

        private class Budget
        {
            public int Remaining;
            public bool Truncated;
        }

        private static object BuildNode(Transform t, int depth, int maxDepth, bool includeComponents, bool includeInactive, Budget budget)
        {
            budget.Remaining--;

            var node = new Dictionary<string, object>
            {
                { "name", t.name },
                { "active", t.gameObject.activeSelf },
                { "tag", t.tag },
                { "layer", LayerMask.LayerToName(t.gameObject.layer) },
                { "instanceId", t.gameObject.GetInstanceID() },
            };

            if (includeComponents)
                node["components"] = InspectionUtil.SummariseComponents(t.gameObject);

            if (depth < maxDepth && t.childCount > 0)
            {
                var children = new List<object>();
                for (int i = 0; i < t.childCount; i++)
                {
                    if (budget.Remaining <= 0) { budget.Truncated = true; break; }
                    var child = t.GetChild(i);
                    if (!includeInactive && !child.gameObject.activeSelf) continue;
                    children.Add(BuildNode(child, depth + 1, maxDepth, includeComponents, includeInactive, budget));
                }
                if (children.Count > 0) node["children"] = children;
            }
            else if (t.childCount > 0)
            {
                node["childCount"] = t.childCount; // depth-capped: report count without expanding
            }

            return node;
        }
    }
}
