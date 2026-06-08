using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor.Compilation;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>
    /// List assembly definitions and read their details. Uses CompilationPipeline (robust, no JSON
    /// file parsing) — covers name, references, defines, unsafe-code, source-file count, flags.
    /// </summary>
    public static class AssemblyDefCommands
    {
        [McpRoute("asmdef/list", "List assembly definitions in the project. Args: term (name filter).")]
        public static object List(JObject args)
        {
            string term = args.Value<string>("term");
            var result = new List<object>();

            foreach (var asm in CompilationPipeline.GetAssemblies(AssembliesType.Editor))
            {
                string asmdefPath = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(asm.name);
                if (string.IsNullOrEmpty(asmdefPath)) continue; // predefined assembly (Assembly-CSharp etc.) — no asmdef
                if (!string.IsNullOrEmpty(term) && asm.name.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0) continue;

                result.Add(new Dictionary<string, object>
                {
                    { "name", asm.name },
                    { "asmdefPath", asmdefPath },
                    { "sourceFileCount", asm.sourceFiles.Length },
                    { "referenceCount", asm.assemblyReferences.Length },
                });
            }

            return new Dictionary<string, object> { { "count", result.Count }, { "assemblies", result } };
        }

        [McpRoute("asmdef/info", "Assembly definition details. Args: one of name or path (asmdef asset path).")]
        public static object Info(JObject args)
        {
            string name = args.Value<string>("name");
            string path = args.Value<string>("path");
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(path))
                return new { error = "Provide 'name' or 'path'." };

            Assembly target = null;
            foreach (var asm in CompilationPipeline.GetAssemblies(AssembliesType.Editor))
            {
                string p = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(asm.name);
                if (string.IsNullOrEmpty(p)) continue;
                if ((!string.IsNullOrEmpty(name) && asm.name == name) || (!string.IsNullOrEmpty(path) && p == path))
                {
                    target = asm;
                    break;
                }
            }
            if (target == null) return new { error = "Assembly definition not found (predefined assemblies have no asmdef)." };

            return new Dictionary<string, object>
            {
                { "name", target.name },
                { "asmdefPath", CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(target.name) },
                { "outputPath", target.outputPath },
                { "sourceFileCount", target.sourceFiles.Length },
                { "references", target.assemblyReferences.Select(a => a.name).ToList() },
                { "precompiledReferenceCount", target.compiledAssemblyReferences.Length },
                { "allowUnsafeCode", target.compilerOptions.AllowUnsafeCode },
                { "flags", target.flags.ToString() },
                // Project-relevant scripting symbols only — drops the ~150 engine UNITY_*/ENABLE_* defines as noise.
                { "customDefines", target.defines.Where(d =>
                    !d.StartsWith("UNITY_") && !d.StartsWith("ENABLE_") && !d.StartsWith("PLATFORM_") &&
                    !d.StartsWith("CSHARP_") && !d.StartsWith("NET_") && !d.StartsWith("TEXTCORE_") &&
                    d != "DEBUG" && d != "TRACE" && d != "INCLUDE_DYNAMIC_GI" && d != "RENDER_SOFTWARE_CURSOR" &&
                    d != "GFXDEVICE_WAITFOREVENT_MESSAGEPUMP" && d != "EDITOR_ONLY_NAVMESH_BUILDER_DEPRECATED").ToList() },
                { "defineCount", target.defines.Length },
            };
        }
    }
}
