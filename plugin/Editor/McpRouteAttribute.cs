using System;

namespace Adanub.UnityMcp.Editor
{
    /// <summary>
    /// Marks a static method as the handler for an MCP bridge route.
    /// The method must have the signature <c>static object Method(Newtonsoft.Json.Linq.JObject args)</c>.
    /// Routes are discovered by <see cref="McpRouteRegistry"/> via reflection, so a new
    /// command module is wired up simply by adding an attributed method — no central switch to edit.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class McpRouteAttribute : Attribute
    {
        /// <summary>The route path, e.g. "ping" or "console/log".</summary>
        public string Route { get; }

        /// <summary>Optional one-line description, surfaced to discovery tooling.</summary>
        public string Description { get; }

        public McpRouteAttribute(string route, string description = null)
        {
            Route = route;
            Description = description;
        }
    }
}
