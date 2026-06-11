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

        /// <summary>
        /// When true the handler runs on the HTTP request (ThreadPool) thread instead of being
        /// marshalled onto the Unity main thread. For long-polling handlers that would otherwise
        /// block the editor; such handlers must not touch Unity APIs directly — use
        /// <c>McpBridgeServer.RunOnMainThread</c> for each state snapshot. The declaring type's
        /// static initialiser also runs on the request thread on first invocation, so declare
        /// these handlers on a type with no static state (see <c>CompileStatusRoute</c>) rather
        /// than on a command class whose initialiser touches Unity APIs.
        /// </summary>
        public bool RunOnRequestThread { get; set; }

        public McpRouteAttribute(string route, string description = null)
        {
            Route = route;
            Description = description;
        }
    }
}
