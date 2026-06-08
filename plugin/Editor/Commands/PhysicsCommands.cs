using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>Read the 3D physics layer collision matrix.</summary>
    public static class PhysicsCommands
    {
        [McpRoute("physics/collision-matrix", "Layer collision matrix (3D). For each named layer, the named layers it collides with.")]
        public static object CollisionMatrix(JObject args)
        {
            var named = new List<int>();
            for (int i = 0; i < 32; i++)
                if (!string.IsNullOrEmpty(LayerMask.LayerToName(i))) named.Add(i);

            var matrix = new List<object>();
            foreach (int i in named)
            {
                var collidesWith = new List<string>();
                foreach (int j in named)
                    if (!Physics.GetIgnoreLayerCollision(i, j)) collidesWith.Add(LayerMask.LayerToName(j));

                matrix.Add(new Dictionary<string, object>
                {
                    { "layer", LayerMask.LayerToName(i) },
                    { "index", i },
                    { "collidesWith", collidesWith },
                });
            }

            return new Dictionary<string, object>
            {
                { "gravity", new Dictionary<string, object> { { "x", Physics.gravity.x }, { "y", Physics.gravity.y }, { "z", Physics.gravity.z } } },
                { "namedLayerCount", named.Count },
                { "matrix", matrix },
            };
        }
    }
}
