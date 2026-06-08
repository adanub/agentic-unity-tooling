using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>
    /// Read-only prefab inspection — works on prefab assets directly (no scene instance) and on
    /// scene prefab instances. Uses PrefabUtility for variant/override information.
    /// </summary>
    public static class PrefabCommands
    {
        private static GameObject LoadPrefab(string path) => AssetDatabase.LoadAssetAtPath<GameObject>(path);

        [McpRoute("prefab/info", "Prefab info. Args: assetPath (a prefab asset) OR a scene GameObject (path/name/instanceId, a prefab instance).")]
        public static object Info(JObject args)
        {
            string assetPath = args.Value<string>("assetPath");
            if (!string.IsNullOrEmpty(assetPath))
            {
                var root = LoadPrefab(assetPath);
                if (root == null) return new { error = $"No prefab at '{assetPath}'." };

                var assetType = PrefabUtility.GetPrefabAssetType(root);
                bool isVariant = assetType == PrefabAssetType.Variant;
                string basePath = null;
                if (isVariant)
                {
                    var baseObj = PrefabUtility.GetCorrespondingObjectFromSource(root);
                    if (baseObj != null) basePath = AssetDatabase.GetAssetPath(baseObj);
                }
                return new Dictionary<string, object>
                {
                    { "kind", "asset" },
                    { "path", assetPath },
                    { "assetType", assetType.ToString() },
                    { "isVariant", isVariant },
                    { "basePrefab", basePath },
                    { "rootName", root.name },
                    { "childCount", root.transform.childCount },
                };
            }

            var go = InspectionUtil.ResolveGameObject(args);
            if (go == null) return new { error = "Provide assetPath (prefab asset) or a scene GameObject (path/name/instanceId)." };
            if (!PrefabUtility.IsPartOfPrefabInstance(go))
                return new { error = $"'{go.name}' is not a prefab instance.", instanceStatus = PrefabUtility.GetPrefabInstanceStatus(go).ToString() };

            var instRoot = PrefabUtility.GetNearestPrefabInstanceRoot(go) ?? go;
            int overrides = 0, addedComps = 0, removedComps = 0, addedGOs = 0;
            try { overrides = PrefabUtility.GetObjectOverrides(instRoot, false).Count; } catch { }
            try { addedComps = PrefabUtility.GetAddedComponents(instRoot).Count; } catch { }
            try { removedComps = PrefabUtility.GetRemovedComponents(instRoot).Count; } catch { }
            try { addedGOs = PrefabUtility.GetAddedGameObjects(instRoot).Count; } catch { }

            return new Dictionary<string, object>
            {
                { "kind", "instance" },
                { "gameObject", InspectionUtil.GetPath(go.transform) },
                { "instanceStatus", PrefabUtility.GetPrefabInstanceStatus(go).ToString() },
                { "sourcePrefab", PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go) },
                { "isVariantSource", PrefabUtility.IsPartOfVariantPrefab(go) },
                { "overrideCount", overrides },
                { "addedComponents", addedComps },
                { "removedComponents", removedComps },
                { "addedGameObjects", addedGOs },
            };
        }

        [McpRoute("prefab/get-hierarchy", "Prefab asset tree from disk (no scene instance). Args: path (prefab asset), maxDepth (8), maxNodes (1000), includeComponents (false).")]
        public static object GetHierarchy(JObject args)
        {
            string path = args.Value<string>("path") ?? args.Value<string>("assetPath");
            if (string.IsNullOrEmpty(path)) return new { error = "Missing 'path' (prefab asset)." };
            var root = LoadPrefab(path);
            if (root == null) return new { error = $"No prefab at '{path}'." };

            int maxDepth = args.Value<int?>("maxDepth") ?? 8;
            int maxNodes = args.Value<int?>("maxNodes") ?? 1000;
            bool includeComponents = args.Value<bool?>("includeComponents") ?? false;

            int[] budget = { maxNodes };
            var node = BuildNode(root.transform, 0, maxDepth, includeComponents, budget);

            return new Dictionary<string, object>
            {
                { "path", path },
                { "nodeCount", maxNodes - budget[0] },
                { "truncated", budget[0] <= 0 },
                { "root", node },
            };
        }

        private static object BuildNode(Transform t, int depth, int maxDepth, bool includeComponents, int[] budget)
        {
            budget[0]--;
            var node = new Dictionary<string, object> { { "name", t.name }, { "active", t.gameObject.activeSelf } };
            if (includeComponents) node["components"] = InspectionUtil.SummariseComponents(t.gameObject);

            if (depth < maxDepth && t.childCount > 0)
            {
                var children = new List<object>();
                for (int i = 0; i < t.childCount && budget[0] > 0; i++)
                    children.Add(BuildNode(t.GetChild(i), depth + 1, maxDepth, includeComponents, budget));
                if (children.Count > 0) node["children"] = children;
            }
            else if (t.childCount > 0)
            {
                node["childCount"] = t.childCount;
            }
            return node;
        }

        [McpRoute("prefab/get-properties", "Component properties inside a prefab asset (no scene instance). Args: path (prefab asset), prefabPath (internal child path, optional), type (component type). Omit type to list components.")]
        public static object GetProperties(JObject args)
        {
            string path = args.Value<string>("path") ?? args.Value<string>("assetPath");
            if (string.IsNullOrEmpty(path)) return new { error = "Missing 'path' (prefab asset)." };
            var root = LoadPrefab(path);
            if (root == null) return new { error = $"No prefab at '{path}'." };

            Transform target = root.transform;
            string internalPath = args.Value<string>("prefabPath");
            if (!string.IsNullOrEmpty(internalPath))
            {
                target = root.transform.Find(internalPath);
                if (target == null) return new { error = $"Child '{internalPath}' not found in prefab '{path}'." };
            }

            string typeName = args.Value<string>("type");
            if (string.IsNullOrEmpty(typeName))
                return new Dictionary<string, object>
                {
                    { "error", "Missing 'type'. Available components below." },
                    { "availableComponents", InspectionUtil.SummariseComponents(target.gameObject) },
                };

            Component comp = null;
            foreach (var c in target.GetComponents<Component>())
                if (c != null && (c.GetType().Name == typeName || c.GetType().FullName == typeName)) { comp = c; break; }
            if (comp == null)
                return new Dictionary<string, object>
                {
                    { "error", $"Component '{typeName}' not found on the target." },
                    { "availableComponents", InspectionUtil.SummariseComponents(target.gameObject) },
                };

            return new Dictionary<string, object>
            {
                { "path", path },
                { "prefabPath", internalPath ?? "" },
                { "component", comp.GetType().Name },
                { "properties", InspectionUtil.SerialiseComponent(comp) },
            };
        }

        [McpRoute("prefab/variant-info", "Variant status of a prefab. Args: path (prefab asset), findVariants (scan for variants derived from this base, default false), limit (50).")]
        public static object VariantInfo(JObject args)
        {
            string path = args.Value<string>("path") ?? args.Value<string>("assetPath");
            if (string.IsNullOrEmpty(path)) return new { error = "Missing 'path' (prefab asset)." };
            var root = LoadPrefab(path);
            if (root == null) return new { error = $"No prefab at '{path}'." };

            bool isVariant = PrefabUtility.GetPrefabAssetType(root) == PrefabAssetType.Variant;
            string basePath = null;
            if (isVariant)
            {
                var baseObj = PrefabUtility.GetCorrespondingObjectFromSource(root);
                if (baseObj != null) basePath = AssetDatabase.GetAssetPath(baseObj);
            }

            var result = new Dictionary<string, object>
            {
                { "path", path },
                { "isVariant", isVariant },
                { "basePrefab", basePath },
            };

            if (args.Value<bool?>("findVariants") ?? false)
            {
                int limit = args.Value<int?>("limit") ?? 50;
                var variants = new List<object>();
                foreach (var guid in AssetDatabase.FindAssets("t:Prefab"))
                {
                    if (variants.Count >= limit) break;
                    string p = AssetDatabase.GUIDToAssetPath(guid);
                    if (p == path) continue;
                    var go = LoadPrefab(p);
                    if (go == null || PrefabUtility.GetPrefabAssetType(go) != PrefabAssetType.Variant) continue;
                    var b = PrefabUtility.GetCorrespondingObjectFromSource(go);
                    if (b != null && AssetDatabase.GetAssetPath(b) == path) variants.Add(p);
                }
                result["derivedVariants"] = variants;
                result["derivedVariantCount"] = variants.Count;
            }
            return result;
        }

        [McpRoute("prefab/compare-variant", "Property overrides a variant applies over its base. Args: path (variant prefab asset), limit (100).")]
        public static object CompareVariant(JObject args)
        {
            string path = args.Value<string>("path") ?? args.Value<string>("assetPath");
            if (string.IsNullOrEmpty(path)) return new { error = "Missing 'path' (variant prefab asset)." };
            var root = LoadPrefab(path);
            if (root == null) return new { error = $"No prefab at '{path}'." };
            if (PrefabUtility.GetPrefabAssetType(root) != PrefabAssetType.Variant)
                return new { error = $"'{path}' is not a prefab variant." };

            int limit = args.Value<int?>("limit") ?? 100;
            var mods = new List<object>();
            try
            {
                var pm = PrefabUtility.GetPropertyModifications(root);
                if (pm is not null)
                {
                    foreach (var m in pm)
                    {
                        if (mods.Count >= limit) break;
                        if (m is null) continue;
                        mods.Add(new Dictionary<string, object>
                        {
                            { "target", m.target != null ? m.target.GetType().Name : null },
                            { "propertyPath", m.propertyPath },
                            { "value", m.value },
                            { "objectReference", m.objectReference != null ? m.objectReference.name : null },
                        });
                    }
                }
            }
            catch (System.Exception ex)
            {
                return new { error = $"Failed to read variant modifications: {ex.Message}" };
            }

            var baseObj = PrefabUtility.GetCorrespondingObjectFromSource(root);
            return new Dictionary<string, object>
            {
                { "path", path },
                { "basePrefab", baseObj != null ? AssetDatabase.GetAssetPath(baseObj) : null },
                { "modificationCount", mods.Count },
                { "modifications", mods },
            };
        }
    }
}
