using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>
    /// Shared helpers for the inspection routes: resolving a GameObject from request args,
    /// building hierarchy paths, and serialising a component's properties to JSON-friendly data.
    /// </summary>
    public static class InspectionUtil
    {
        /// <summary>
        /// Resolve a GameObject from args. Accepts (in priority order): instanceId, path
        /// (e.g. "Parent/Child"), or name (first match, including inactive). Returns null if none match.
        /// </summary>
        public static GameObject ResolveGameObject(JObject args)
        {
            int? instanceId = args.Value<int?>("instanceId");
            if (instanceId.HasValue)
            {
                if (FindByInstanceId(instanceId.Value) is GameObject go) return go;
            }

            string path = args.Value<string>("path");
            if (!string.IsNullOrEmpty(path))
            {
                var byPath = FindByPath(path);
                if (byPath != null) return byPath;
            }

            string name = args.Value<string>("name");
            if (!string.IsNullOrEmpty(name))
            {
                foreach (var g in UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (g.name == name) return g;
            }

            return null;
        }

        /// <summary>Find a GameObject by full hierarchy path ("Root/Child/Leaf") across loaded scenes.</summary>
        public static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string[] parts = path.Split('/');

            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root.name != parts[0]) continue;
                    var current = root.transform;
                    bool ok = true;
                    for (int i = 1; i < parts.Length; i++)
                    {
                        var child = current.Find(parts[i]);
                        if (child == null) { ok = false; break; }
                        current = child;
                    }
                    if (ok) return current.gameObject;
                }
            }
            return null;
        }

        /// <summary>Build the full hierarchy path of a transform ("Root/Child/Leaf").</summary>
        public static string GetPath(Transform t)
        {
            if (t == null) return "";
            var stack = new Stack<string>();
            while (t != null)
            {
                stack.Push(t.name);
                t = t.parent;
            }
            return string.Join("/", stack);
        }

        /// <summary>Serialise a component's top-level visible serialized properties to a dictionary.</summary>
        public static Dictionary<string, object> SerialiseComponent(Component component, int maxProps = 200)
            => SerialiseUnityObject(component, maxProps);

        /// <summary>
        /// Serialise any UnityEngine.Object's top-level visible serialized properties (components,
        /// ScriptableObjects, assets) — captures [SerializeField] privates as the inspector sees them.
        /// </summary>
        public static Dictionary<string, object> SerialiseUnityObject(UnityEngine.Object obj, int maxProps = 200)
        {
            var dict = new Dictionary<string, object>();
            if (obj == null) return dict;

            using (var so = new SerializedObject(obj))
            {
                var prop = so.GetIterator();
                int count = 0;
                if (prop.NextVisible(true))
                {
                    do
                    {
                        if (prop.name == "m_Script") continue;
                        dict[prop.name] = SerialiseProperty(prop);
                        count++;
                    }
                    while (count < maxProps && prop.NextVisible(false));
                }
            }
            return dict;
        }

        public static object SerialiseProperty(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: return p.intValue;
                case SerializedPropertyType.Boolean: return p.boolValue;
                case SerializedPropertyType.Float: return p.doubleValue;
                case SerializedPropertyType.String: return p.stringValue;
                case SerializedPropertyType.LayerMask: return p.intValue;
                case SerializedPropertyType.ArraySize: return p.intValue;
                case SerializedPropertyType.Character: return p.intValue;
                case SerializedPropertyType.Enum:
                    return (p.enumValueIndex >= 0 && p.enumNames is not null && p.enumValueIndex < p.enumNames.Length)
                        ? (object)p.enumNames[p.enumValueIndex]
                        : p.intValue;
                case SerializedPropertyType.Color:
                {
                    var c = p.colorValue;
                    return new Dictionary<string, object> { { "r", c.r }, { "g", c.g }, { "b", c.b }, { "a", c.a } };
                }
                case SerializedPropertyType.Vector2:
                {
                    var v = p.vector2Value;
                    return new Dictionary<string, object> { { "x", v.x }, { "y", v.y } };
                }
                case SerializedPropertyType.Vector3:
                {
                    var v = p.vector3Value;
                    return new Dictionary<string, object> { { "x", v.x }, { "y", v.y }, { "z", v.z } };
                }
                case SerializedPropertyType.Vector4:
                {
                    var v = p.vector4Value;
                    return new Dictionary<string, object> { { "x", v.x }, { "y", v.y }, { "z", v.z }, { "w", v.w } };
                }
                case SerializedPropertyType.Quaternion:
                {
                    var q = p.quaternionValue.eulerAngles;
                    return new Dictionary<string, object> { { "euler", new Dictionary<string, object> { { "x", q.x }, { "y", q.y }, { "z", q.z } } } };
                }
                case SerializedPropertyType.ObjectReference:
                {
                    var o = p.objectReferenceValue;
                    if (o == null) return null;
                    return new Dictionary<string, object>
                    {
                        { "name", o.name },
                        { "type", o.GetType().Name },
                        { "assetPath", AssetDatabase.GetAssetPath(o) },
                    };
                }
                default:
                    // Generic/managed-ref/struct etc. — report the kind rather than a deep dump.
                    return $"<{p.propertyType}>";
            }
        }

        /// <summary>
        /// Resolve an Object from an integer instance id. Centralised so the single
        /// deprecated call lives in one place: Unity 6.3 marks EditorUtility.InstanceIDToObject(int)
        /// obsolete in favour of EntityIdToObject(EntityId), but our JSON API uses int instance
        /// ids throughout (GetInstanceID() is not deprecated), so the int lookup stays consistent.
        /// </summary>
        public static UnityEngine.Object FindByInstanceId(int instanceId)
        {
#pragma warning disable CS0618 // int InstanceIDToObject kept deliberately — see summary
            return EditorUtility.InstanceIDToObject(instanceId);
#pragma warning restore CS0618
        }

        /// <summary>Resolve a System.Type from a short name or full name, scanning loaded assemblies.</summary>
        public static Type FindType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            var direct = Type.GetType(name)
                ?? Type.GetType($"UnityEngine.{name}, UnityEngine")
                ?? Type.GetType($"UnityEditor.{name}, UnityEditor");
            if (direct is not null) return direct;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }
                foreach (var t in types)
                    if (t.Name == name || t.FullName == name) return t;
            }
            return null;
        }

        /// <summary>Summarise a GameObject's components as type names (with enabled state where applicable).</summary>
        public static List<object> SummariseComponents(GameObject go)
        {
            var list = new List<object>();
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) { list.Add(new Dictionary<string, object> { { "type", "<missing script>" } }); continue; }
                var entry = new Dictionary<string, object> { { "type", c.GetType().Name } };
                if (c is Behaviour b) entry["enabled"] = b.enabled;
                else if (c is Renderer r) entry["enabled"] = r.enabled;
                else if (c is Collider col) entry["enabled"] = col.enabled;
                list.Add(entry);
            }
            return list;
        }
    }
}
