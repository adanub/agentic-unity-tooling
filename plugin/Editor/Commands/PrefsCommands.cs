using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>Read EditorPrefs / PlayerPrefs values.</summary>
    public static class PrefsCommands
    {
        [McpRoute("editorprefs/get", "Read an EditorPrefs value. Args: key (required), type (string|int|float|bool, default string).")]
        public static object GetEditorPref(JObject args)
        {
            string key = args.Value<string>("key");
            if (string.IsNullOrEmpty(key)) return new { error = "Missing 'key'." };
            if (!EditorPrefs.HasKey(key)) return new Dictionary<string, object> { { "key", key }, { "exists", false } };

            string type = (args.Value<string>("type") ?? "string").ToLowerInvariant();
            object value = type switch
            {
                "int" => (object)EditorPrefs.GetInt(key),
                "float" => EditorPrefs.GetFloat(key),
                "bool" => EditorPrefs.GetBool(key),
                _ => EditorPrefs.GetString(key),
            };
            return new Dictionary<string, object> { { "key", key }, { "exists", true }, { "type", type }, { "value", value } };
        }

        [McpRoute("playerprefs/get", "Read a PlayerPrefs value. Args: key (required), type (string|int|float, default string).")]
        public static object GetPlayerPref(JObject args)
        {
            string key = args.Value<string>("key");
            if (string.IsNullOrEmpty(key)) return new { error = "Missing 'key'." };
            if (!PlayerPrefs.HasKey(key)) return new Dictionary<string, object> { { "key", key }, { "exists", false } };

            string type = (args.Value<string>("type") ?? "string").ToLowerInvariant();
            object value = type switch
            {
                "int" => (object)PlayerPrefs.GetInt(key),
                "float" => PlayerPrefs.GetFloat(key),
                _ => PlayerPrefs.GetString(key),
            };
            return new Dictionary<string, object> { { "key", key }, { "exists", true }, { "type", type }, { "value", value } };
        }
    }
}
