using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor.PackageManager;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>
    /// List installed packages and read package details. Uses the synchronous
    /// PackageInfo.GetAllRegisteredPackages() (no async Client requests / polling).
    /// </summary>
    public static class PackagesCommands
    {
        [McpRoute("packages/list", "List installed packages. Args: term (name/displayName filter).")]
        public static object List(JObject args)
        {
            string term = args.Value<string>("term");
            var packages = PackageInfo.GetAllRegisteredPackages().OrderBy(p => p.name);

            var result = new List<object>();
            foreach (var p in packages)
            {
                if (!string.IsNullOrEmpty(term))
                {
                    bool match = p.name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0
                        || (p.displayName != null && p.displayName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!match) continue;
                }
                result.Add(new Dictionary<string, object>
                {
                    { "name", p.name },
                    { "displayName", p.displayName },
                    { "version", p.version },
                    { "source", p.source.ToString() },
                });
            }
            return new Dictionary<string, object> { { "count", result.Count }, { "packages", result } };
        }

        [McpRoute("packages/info", "Package details. Args: name (package id, e.g. 'com.unity.render-pipelines.universal').")]
        public static object Info(JObject args)
        {
            string name = args.Value<string>("name");
            if (string.IsNullOrEmpty(name)) return new { error = "Missing 'name'." };

            var p = PackageInfo.GetAllRegisteredPackages().FirstOrDefault(x => x.name == name);
            if (p == null) return new { error = $"Package '{name}' not found among registered packages." };

            return new Dictionary<string, object>
            {
                { "name", p.name },
                { "displayName", p.displayName },
                { "version", p.version },
                { "description", p.description },
                { "category", p.category },
                { "source", p.source.ToString() },
                { "resolvedPath", p.resolvedPath },
                { "dependencies", p.dependencies.Select(d => d.name + "@" + d.version).ToList() },
            };
        }
    }
}
