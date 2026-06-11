using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Adanub.UnityMcp.Editor
{
    /// <summary>
    /// Discovers and dispatches MCP bridge routes.
    ///
    /// Replaces the giant hand-maintained switch statement with reflection-based
    /// registration: any static method decorated with <see cref="McpRouteAttribute"/>
    /// in this assembly becomes a callable route. Command modules are therefore fully
    /// self-contained — adding one never requires touching the bridge or this registry.
    /// </summary>
    public static class McpRouteRegistry
    {
        public readonly struct RouteEntry
        {
            public readonly string Route;
            public readonly string Description;
            public readonly Func<JObject, object> Handler;
            public readonly bool RunOnRequestThread;

            public RouteEntry(string route, string description, Func<JObject, object> handler, bool runOnRequestThread)
            {
                Route = route;
                Description = description;
                Handler = handler;
                RunOnRequestThread = runOnRequestThread;
            }
        }

        // Reflection scan results are immutable for the assembly's lifetime, so the built map is
        // cached. A domain reload (recompile / play-mode entry) resets this to null with the
        // assembly and the lazy getter rebuilds — no explicit reset hook needed. (A
        // RuntimeInitializeOnLoadMethod reset would be wrong here: in this editor-only assembly it
        // fires on play-mode entry and, under Disable Domain Reload, would null a still-valid cache.)
        // Request-thread routes look the registry up off the main thread, so the lazy build is
        // double-checked-locked and publishes a fully-populated map (readers never see a partial one).
        private static volatile Dictionary<string, RouteEntry> _routes;
        private static readonly object _buildLock = new object();

        private static Dictionary<string, RouteEntry> Routes
        {
            get
            {
                if (_routes is null)
                {
                    lock (_buildLock)
                    {
                        if (_routes is null) _routes = Build();
                    }
                }
                return _routes;
            }
        }

        private static Dictionary<string, RouteEntry> Build()
        {
            var routes = new Dictionary<string, RouteEntry>(StringComparer.Ordinal);

            var assembly = typeof(McpRouteRegistry).Assembly;
            foreach (var type in assembly.GetTypes())
            {
                MethodInfo[] methods;
                try
                {
                    methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                }
                catch
                {
                    continue;
                }

                foreach (var method in methods)
                {
                    var attr = method.GetCustomAttribute<McpRouteAttribute>();
                    if (attr == null) continue;

                    if (!IsValidSignature(method))
                    {
                        Debug.LogError(
                            $"[Adanub MCP] Route '{attr.Route}' on {type.Name}.{method.Name} has an invalid " +
                            "signature. Expected: static object Method(JObject args).");
                        continue;
                    }

                    if (routes.ContainsKey(attr.Route))
                    {
                        Debug.LogError($"[Adanub MCP] Duplicate route '{attr.Route}' — ignoring {type.Name}.{method.Name}.");
                        continue;
                    }

                    var captured = method;
                    Func<JObject, object> handler = args => captured.Invoke(null, new object[] { args });
                    routes[attr.Route] = new RouteEntry(attr.Route, attr.Description, handler, attr.RunOnRequestThread);
                }
            }

            return routes;
        }

        private static bool IsValidSignature(MethodInfo method)
        {
            if (method.ReturnType != typeof(object)) return false;
            var parameters = method.GetParameters();
            return parameters.Length == 1 && parameters[0].ParameterType == typeof(JObject);
        }

        /// <summary>Look up a route handler. Returns false if the route is not registered.</summary>
        public static bool TryGet(string route, out RouteEntry entry)
        {
            return Routes.TryGetValue(route, out entry);
        }

        /// <summary>All registered routes (for discovery / introspection).</summary>
        public static IEnumerable<RouteEntry> All => Routes.Values;
    }
}
