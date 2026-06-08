using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditorInternal;
using UnityEngine;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>Read the project's tags, layers, and sorting layers.</summary>
    public static class TagLayerCommands
    {
        [McpRoute("taglayer/info", "Project tags, layers (index+name), and sorting layers.")]
        public static object Info(JObject args)
        {
            var layers = new List<object>();
            for (int i = 0; i < 32; i++)
            {
                string name = LayerMask.LayerToName(i);
                if (!string.IsNullOrEmpty(name))
                    layers.Add(new Dictionary<string, object> { { "index", i }, { "name", name } });
            }

            var sorting = new List<object>();
            foreach (var s in SortingLayer.layers)
                sorting.Add(new Dictionary<string, object> { { "id", s.id }, { "name", s.name }, { "value", s.value } });

            return new Dictionary<string, object>
            {
                { "tags", InternalEditorUtility.tags },
                { "layers", layers },
                { "sortingLayers", sorting },
            };
        }
    }
}
