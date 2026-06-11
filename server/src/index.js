#!/usr/bin/env node
// Adanub Unity MCP — stdio MCP server.
//
// Thin shim: exposes MCP tools that forward to a Unity Editor HTTP bridge. Supports
// multiple editors at once (game client + server) via discovery + selection; every
// forwarding tool also accepts an optional `port` for explicit parallel-safe routing.

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";

import {
  discoverInstances,
  selectInstance,
  resolveTargetPort,
  describeInstances,
  callWithRecovery,
} from "./instances.js";

// ─── Forwarding tools (each maps to a bridge route) ───
// Grows by appending entries — no dispatch logic to edit.
const TOOLS = [
  {
    name: "unity_editor_ping",
    description:
      "Check that a Unity Editor bridge is responsive. Returns Unity version, project " +
      "name/path, bound port, play/compile state, and process id.",
    inputSchema: { type: "object", properties: {} },
    route: "ping",
  },

  // ── Console & compilation ──
  {
    name: "unity_console_log",
    description:
      "Read the Unity Console (reads Unity's own store, reflecting its type toggles + search filter). " +
      "Collapses identical messages with a repeat count by default — ideal for cutting per-frame log spam.",
    inputSchema: {
      type: "object",
      properties: {
        collapse: { type: "boolean", description: "Dedupe identical messages into one entry with a 'count' (default true). Set false for raw chronological entries." },
        count: { type: "number", description: "Max entries to return (default 50, most recent)." },
        type: { type: "string", enum: ["all", "error", "warning", "info"], description: "Filter by log type (default all)." },
        match: { type: "string", description: "Only entries whose message contains this substring (or regex if 'regex' is true)." },
        regex: { type: "boolean", description: "Treat 'match' as a case-insensitive regex (default false)." },
        includeStackTrace: { type: "boolean", description: "Include each entry's stack trace (default false — keeps responses small)." },
      },
    },
    route: "console/log",
  },
  {
    name: "unity_console_clear",
    description: "Clear the Unity Console (clears Unity's actual console store).",
    inputSchema: { type: "object", properties: {} },
    route: "console/clear",
    mutates: true,
  },
  {
    name: "unity_compilation_errors",
    description: "Compiler errors/warnings from the last compile (survives console clears).",
    inputSchema: {
      type: "object",
      properties: {
        count: { type: "number", description: "Max entries (default 50)." },
        severity: { type: "string", enum: ["all", "error", "warning"], description: "Filter (default all)." },
      },
    },
    route: "compilation/errors",
  },
  {
    name: "unity_compile_request",
    description:
      "Trigger Unity to pick up script changes from disk (AssetDatabase.Refresh) and compile them — works " +
      "without focusing the editor. Use after editing .cs files, then call unity_compile_status with waitMs " +
      "to wait for the result.",
    inputSchema: { type: "object", properties: {} },
    route: "compile/request",
    mutates: true,
  },
  {
    name: "unity_compile_status",
    description:
      "Status/result of the compile session started by unity_compile_request. Phases: refreshQueued → " +
      "waitingForCompile → compiling → finished; results: clean | errors | noCompile. Returns compiler " +
      "errors/warnings. A clean compile's domain reload may briefly drop the bridge mid-call — the server " +
      "retries automatically, so just await the result. Caveat: in play mode the editor may defer compiles " +
      "until play exits, so noCompile + isPlaying:true is inconclusive.",
    inputSchema: {
      type: "object",
      properties: {
        waitMs: { type: "number", description: "Long-poll up to this many ms for phase=finished (0-25000, default 0 = immediate snapshot)." },
        count: { type: "number", description: "Max compiler messages returned (default 50)." },
      },
    },
    route: "compile/status",
  },

  // ── Editor / project / scene state ──
  {
    name: "unity_editor_state",
    description: "Editor state: play/pause/compile/update flags, active scene, selection count.",
    inputSchema: { type: "object", properties: {} },
    route: "editor/state",
  },
  {
    name: "unity_project_info",
    description: "Project info: name, paths, Unity version, render pipeline, build target, scene count.",
    inputSchema: { type: "object", properties: {} },
    route: "project/info",
  },
  {
    name: "unity_scene_info",
    description: "Open scene(s): name, path, loaded/dirty/active state, root object count.",
    inputSchema: { type: "object", properties: {} },
    route: "scene/info",
  },
  {
    name: "unity_scene_stats",
    description: "Scene totals: objects, renderers, lights, cameras, colliders, approx verts/tris.",
    inputSchema: { type: "object", properties: {} },
    route: "scene/stats",
  },

  // ── Profiler ──
  {
    name: "unity_profiler_stats",
    description: "Rendering stats (draw calls, batches, tris, verts, set-pass, frame/render time). Most meaningful in Play mode.",
    inputSchema: { type: "object", properties: {} },
    route: "profiler/stats",
  },
  {
    name: "unity_profiler_memory",
    description: "Memory usage: total/reserved, Mono heap + fragmentation, gfx driver, temp allocator (bytes + MB).",
    inputSchema: { type: "object", properties: {} },
    route: "profiler/memory",
  },
  {
    name: "unity_profiler_frame_data",
    description: "CPU timing hierarchy for a captured frame. Requires the Profiler to be recording.",
    inputSchema: {
      type: "object",
      properties: {
        frameIndex: { type: "number", description: "Frame to read (default: latest captured)." },
        maxItems: { type: "number", description: "Max hierarchy rows (default 30)." },
        minTimeMs: { type: "number", description: "Drop entries below this total ms (default 0)." },
        threadIndex: { type: "number", description: "Thread index (default 0 = main)." },
      },
    },
    route: "profiler/frame-data",
  },
  {
    name: "unity_profiler_analyze",
    description: "Combined snapshot: memory + (Play-mode) rendering + (if recording) CPU hotspots + scene complexity + suggestions.",
    inputSchema: { type: "object", properties: {} },
    route: "profiler/analyze",
  },

  // ── Memory (asset) ──
  {
    name: "unity_memory_status",
    description: "Memory summary + whether the com.unity.memoryprofiler package is installed.",
    inputSchema: { type: "object", properties: {} },
    route: "memory/status",
  },
  {
    name: "unity_memory_breakdown",
    description: "Memory by asset type (textures, meshes, materials, shaders, audio, animation, fonts, RTs, SOs).",
    inputSchema: { type: "object", properties: {} },
    route: "memory/breakdown",
  },
  {
    name: "unity_memory_top_assets",
    description: "Largest individual assets in memory, with paths.",
    inputSchema: {
      type: "object",
      properties: {
        limit: { type: "number", description: "Max assets to return (default 25)." },
        type: {
          type: "string",
          enum: ["texture", "mesh", "material", "shader", "audio", "animation", "font", "rendertexture"],
          description: "Optional asset-type filter.",
        },
      },
    },
    route: "memory/top-assets",
  },

  // ── Scene hierarchy & search ──
  {
    name: "unity_scene_hierarchy",
    description: "GameObject tree of loaded scenes (bounded). Use parentPath to scope to a subtree.",
    inputSchema: {
      type: "object",
      properties: {
        maxDepth: { type: "number", description: "Max tree depth (default 8)." },
        maxNodes: { type: "number", description: "Max nodes returned (default 2000)." },
        parentPath: { type: "string", description: "Only dump the subtree under this GameObject path." },
        includeComponents: { type: "boolean", description: "Include each node's component type list (default false)." },
        includeInactive: { type: "boolean", description: "Include inactive objects (default true)." },
      },
    },
    route: "scene/hierarchy",
  },
  {
    name: "unity_search_by_name",
    description: "Find GameObjects whose name matches a substring or regex.",
    inputSchema: {
      type: "object",
      properties: {
        query: { type: "string", description: "Name substring (or regex if regex=true)." },
        regex: { type: "boolean", description: "Treat query as a regex (default false)." },
        limit: { type: "number", description: "Max results (default 200)." },
      },
      required: ["query"],
    },
    route: "search/by-name",
  },
  {
    name: "unity_search_by_component",
    description: "Find GameObjects that have a given component type.",
    inputSchema: {
      type: "object",
      properties: {
        type: { type: "string", description: "Component type name, e.g. 'Rigidbody' or a script class name." },
        limit: { type: "number", description: "Max results (default 200)." },
      },
      required: ["type"],
    },
    route: "search/by-component",
  },
  {
    name: "unity_search_by_tag",
    description: "Find GameObjects by tag.",
    inputSchema: {
      type: "object",
      properties: { tag: { type: "string" }, limit: { type: "number" } },
      required: ["tag"],
    },
    route: "search/by-tag",
  },
  {
    name: "unity_search_by_layer",
    description: "Find GameObjects on a layer (name or index).",
    inputSchema: {
      type: "object",
      properties: { layer: { type: "string", description: "Layer name or index." }, limit: { type: "number" } },
      required: ["layer"],
    },
    route: "search/by-layer",
  },
  {
    name: "unity_search_by_shader",
    description: "Find renderers using a shader (name substring).",
    inputSchema: {
      type: "object",
      properties: { shader: { type: "string" }, limit: { type: "number" } },
      required: ["shader"],
    },
    route: "search/by-shader",
  },
  {
    name: "unity_search_assets",
    description: "Search project assets via AssetDatabase filter (e.g. 't:Material name').",
    inputSchema: {
      type: "object",
      properties: {
        filter: { type: "string", description: "AssetDatabase filter string." },
        folder: { type: "string", description: "Optional folder to scope the search." },
        limit: { type: "number", description: "Max results (default 200)." },
      },
    },
    route: "search/assets",
  },
  {
    name: "unity_search_missing_references",
    description: "Find missing scripts and broken object references in loaded scenes.",
    inputSchema: { type: "object", properties: { limit: { type: "number" } } },
    route: "search/missing-references",
  },

  // ── Selection ──
  {
    name: "unity_selection_get",
    description: "Currently selected GameObjects in the editor.",
    inputSchema: { type: "object", properties: {} },
    route: "selection/get",
  },
  {
    name: "unity_selection_find_by_type",
    description: "Find GameObjects with a component type (alias of search_by_component).",
    inputSchema: {
      type: "object",
      properties: { type: { type: "string" }, limit: { type: "number" } },
      required: ["type"],
    },
    route: "selection/find-by-type",
  },
  {
    name: "unity_selection_set",
    description: "Set the editor selection (changes editor state).",
    inputSchema: {
      type: "object",
      properties: {
        paths: { type: "array", items: { type: "string" }, description: "GameObject paths to select." },
        instanceIds: { type: "array", items: { type: "number" }, description: "Instance ids to select." },
      },
    },
    route: "selection/set",
    mutates: true,
  },
  {
    name: "unity_selection_focus_scene_view",
    description: "Frame the scene-view camera on a GameObject or the current selection (changes scene-view camera).",
    inputSchema: {
      type: "object",
      properties: {
        path: { type: "string" },
        name: { type: "string" },
        instanceId: { type: "number" },
      },
    },
    route: "selection/focus-scene-view",
    mutates: true,
  },

  // ── GameObject / component inspection ──
  {
    name: "unity_gameobject_info",
    description: "Detail for one GameObject: transform, components, children, tag, layer. Identify by path, name, or instanceId.",
    inputSchema: {
      type: "object",
      properties: {
        path: { type: "string", description: "Full hierarchy path, e.g. 'Canvas/Panel/Button'." },
        name: { type: "string", description: "Name (first match)." },
        instanceId: { type: "number" },
      },
    },
    route: "gameobject/info",
  },
  {
    name: "unity_component_get_properties",
    description: "Serialized properties of a component on a GameObject. Identify the GameObject by path/name/instanceId and the component by type.",
    inputSchema: {
      type: "object",
      properties: {
        path: { type: "string" },
        name: { type: "string" },
        instanceId: { type: "number" },
        type: { type: "string", description: "Component type name. Omit to list available components." },
      },
    },
    route: "component/get-properties",
  },
  {
    name: "unity_component_get_referenceable",
    description: "Scene objects and project assets assignable to a given type.",
    inputSchema: {
      type: "object",
      properties: {
        type: { type: "string", description: "Type name, e.g. 'Material', 'Rigidbody', 'AudioClip'." },
        limit: { type: "number" },
      },
      required: ["type"],
    },
    route: "component/get-referenceable",
  },

  // ── Asset / script / ScriptableObject / shader readers ──
  {
    name: "unity_asset_list",
    description: "List project assets, filterable by folder, type, and name term.",
    inputSchema: {
      type: "object",
      properties: {
        folder: { type: "string", description: "Scope to a folder, e.g. 'Assets/Art'." },
        type: { type: "string", description: "Asset type filter, e.g. 'Material', 'Texture2D'." },
        term: { type: "string", description: "Name filter." },
        limit: { type: "number", description: "Max results (default 200)." },
      },
    },
    route: "asset/list",
  },
  {
    name: "unity_script_read",
    description: "Read a C#/text asset's contents (capped). Identify by project-relative path.",
    inputSchema: {
      type: "object",
      properties: {
        path: { type: "string", description: "Project-relative path, e.g. 'Assets/Scripts/Foo.cs'." },
        maxChars: { type: "number", description: "Max characters (default 60000)." },
      },
      required: ["path"],
    },
    route: "script/read",
  },
  {
    name: "unity_scriptableobject_info",
    description: "Serialized properties of a ScriptableObject asset.",
    inputSchema: {
      type: "object",
      properties: { path: { type: "string", description: "Asset path, e.g. 'Assets/.../Config.asset'." } },
      required: ["path"],
    },
    route: "scriptableobject/info",
  },
  {
    name: "unity_scriptableobject_list_types",
    description: "List non-abstract ScriptableObject types defined in the project.",
    inputSchema: {
      type: "object",
      properties: {
        term: { type: "string", description: "Name filter." },
        limit: { type: "number" },
      },
    },
    route: "scriptableobject/list-types",
  },
  {
    name: "unity_shader_list",
    description: "List shader assets (.shader + .shadergraph).",
    inputSchema: {
      type: "object",
      properties: { term: { type: "string" }, limit: { type: "number" } },
    },
    route: "shader/list",
  },
  {
    name: "unity_shader_get_properties",
    description: "Exposed properties of a shader. Identify by asset path or shader name.",
    inputSchema: {
      type: "object",
      properties: {
        path: { type: "string", description: "Shader asset path." },
        name: { type: "string", description: "Shader name, e.g. 'Universal Render Pipeline/Lit'." },
      },
    },
    route: "shader/get-properties",
  },

  // ── Graphics info (target via assetPath or a GameObject) ──
  {
    name: "unity_graphics_mesh_info",
    description: "Mesh stats (verts/tris/submeshes/bounds). Target: mesh assetPath, or a GameObject with a MeshFilter/SkinnedMeshRenderer.",
    inputSchema: {
      type: "object",
      properties: {
        assetPath: { type: "string" },
        path: { type: "string" },
        name: { type: "string" },
        instanceId: { type: "number" },
      },
    },
    route: "graphics/mesh-info",
  },
  {
    name: "unity_graphics_material_info",
    description: "Material details (shader, render queue, keywords). Target: material assetPath, or a GameObject's renderer.",
    inputSchema: {
      type: "object",
      properties: {
        assetPath: { type: "string" },
        path: { type: "string" },
        name: { type: "string" },
        instanceId: { type: "number" },
      },
    },
    route: "graphics/material-info",
  },
  {
    name: "unity_graphics_texture_info",
    description: "Texture runtime details (size, format, mips, filter/wrap).",
    inputSchema: {
      type: "object",
      properties: { assetPath: { type: "string", description: "Texture asset path." } },
      required: ["assetPath"],
    },
    route: "graphics/texture-info",
  },
  {
    name: "unity_graphics_renderer_info",
    description: "Renderer details (materials, bounds, sorting, shadows). Target: a GameObject.",
    inputSchema: {
      type: "object",
      properties: {
        path: { type: "string" },
        name: { type: "string" },
        instanceId: { type: "number" },
      },
    },
    route: "graphics/renderer-info",
  },
  {
    name: "unity_graphics_lighting_summary",
    description: "Scene lighting overview: ambient, fog, skybox, light counts by type.",
    inputSchema: { type: "object", properties: {} },
    route: "graphics/lighting-summary",
  },

  // ── Prefab inspection ──
  {
    name: "unity_prefab_info",
    description: "Prefab info: asset type / variant + base, or (for a scene instance) override counts. Target by assetPath (asset) or a scene GameObject.",
    inputSchema: {
      type: "object",
      properties: {
        assetPath: { type: "string", description: "Prefab asset path." },
        path: { type: "string", description: "Scene GameObject path (a prefab instance)." },
        name: { type: "string" },
        instanceId: { type: "number" },
      },
    },
    route: "prefab/info",
  },
  {
    name: "unity_prefab_get_hierarchy",
    description: "GameObject tree of a prefab asset, read from disk (no scene instance needed).",
    inputSchema: {
      type: "object",
      properties: {
        path: { type: "string", description: "Prefab asset path." },
        maxDepth: { type: "number" },
        maxNodes: { type: "number" },
        includeComponents: { type: "boolean" },
      },
      required: ["path"],
    },
    route: "prefab/get-hierarchy",
  },
  {
    name: "unity_prefab_get_properties",
    description: "Serialized properties of a component inside a prefab asset (no scene instance).",
    inputSchema: {
      type: "object",
      properties: {
        path: { type: "string", description: "Prefab asset path." },
        prefabPath: { type: "string", description: "Internal child path within the prefab, e.g. 'Body/Head'." },
        type: { type: "string", description: "Component type name. Omit to list components." },
      },
      required: ["path"],
    },
    route: "prefab/get-properties",
  },
  {
    name: "unity_prefab_variant_info",
    description: "Variant status of a prefab (is it a variant, its base). Optionally scan for variants derived from it.",
    inputSchema: {
      type: "object",
      properties: {
        path: { type: "string", description: "Prefab asset path." },
        findVariants: { type: "boolean", description: "Scan the project for variants derived from this prefab (default false)." },
        limit: { type: "number" },
      },
      required: ["path"],
    },
    route: "prefab/variant-info",
  },
  {
    name: "unity_prefab_compare_variant",
    description: "Property overrides a variant prefab applies over its base prefab.",
    inputSchema: {
      type: "object",
      properties: {
        path: { type: "string", description: "Variant prefab asset path." },
        limit: { type: "number" },
      },
      required: ["path"],
    },
    route: "prefab/compare-variant",
  },

  // ── Misc readers (3a-1) ──
  {
    name: "unity_taglayer_info",
    description: "Project tags, layers (index+name), and sorting layers.",
    inputSchema: { type: "object", properties: {} },
    route: "taglayer/info",
  },
  {
    name: "unity_sceneview_info",
    description: "Active Scene View camera state: pivot, rotation, size, ortho/2D mode, camera position.",
    inputSchema: { type: "object", properties: {} },
    route: "sceneview/info",
  },
  {
    name: "unity_physics_collision_matrix",
    description: "3D physics layer collision matrix: for each named layer, which named layers it collides with.",
    inputSchema: { type: "object", properties: {} },
    route: "physics/collision-matrix",
  },
  {
    name: "unity_texture_info",
    description: "Texture import settings (type, compression, max size, sprite mode, filter/wrap, mipmaps, sRGB).",
    inputSchema: {
      type: "object",
      properties: { path: { type: "string", description: "Texture asset path." } },
      required: ["path"],
    },
    route: "texture/info",
  },
  {
    name: "unity_editorprefs_get",
    description: "Read an EditorPrefs value by key.",
    inputSchema: {
      type: "object",
      properties: {
        key: { type: "string" },
        type: { type: "string", enum: ["string", "int", "float", "bool"], description: "Value type (default string)." },
      },
      required: ["key"],
    },
    route: "editorprefs/get",
  },
  {
    name: "unity_playerprefs_get",
    description: "Read a PlayerPrefs value by key.",
    inputSchema: {
      type: "object",
      properties: {
        key: { type: "string" },
        type: { type: "string", enum: ["string", "int", "float"], description: "Value type (default string)." },
      },
      required: ["key"],
    },
    route: "playerprefs/get",
  },

  // ── Misc readers (3a-2) ──
  {
    name: "unity_asmdef_list",
    description: "List assembly definitions (name, asmdef path, source-file + reference counts).",
    inputSchema: {
      type: "object",
      properties: { term: { type: "string", description: "Name filter." } },
    },
    route: "asmdef/list",
  },
  {
    name: "unity_asmdef_info",
    description: "Assembly definition details: references, defines, unsafe-code, flags. Identify by name or asmdef path.",
    inputSchema: {
      type: "object",
      properties: {
        name: { type: "string", description: "Assembly name." },
        path: { type: "string", description: "Asmdef asset path." },
      },
    },
    route: "asmdef/info",
  },
  {
    name: "unity_spriteatlas_list",
    description: "List SpriteAtlas assets.",
    inputSchema: { type: "object", properties: { limit: { type: "number" } } },
    route: "spriteatlas/list",
  },
  {
    name: "unity_spriteatlas_info",
    description: "SpriteAtlas details: sprite count, variant flag, packables.",
    inputSchema: {
      type: "object",
      properties: { path: { type: "string", description: "SpriteAtlas asset path." } },
      required: ["path"],
    },
    route: "spriteatlas/info",
  },
  {
    name: "unity_input_info",
    description: "Input Action Asset summary (maps, actions, bindings, control schemes).",
    inputSchema: {
      type: "object",
      properties: { path: { type: "string", description: ".inputactions asset path." } },
      required: ["path"],
    },
    route: "input/info",
  },
  {
    name: "unity_packages_list",
    description: "List installed packages (name, displayName, version, source).",
    inputSchema: {
      type: "object",
      properties: { term: { type: "string", description: "Name/displayName filter." } },
    },
    route: "packages/list",
  },
  {
    name: "unity_packages_info",
    description: "Package details: version, description, dependencies, resolved path. Identify by package id.",
    inputSchema: {
      type: "object",
      properties: { name: { type: "string", description: "Package id, e.g. 'com.unity.render-pipelines.universal'." } },
      required: ["name"],
    },
    route: "packages/info",
  },
];

const TOOLS_BY_NAME = new Map(TOOLS.map((t) => [t.name, t]));

// ─── Instance-management tools (handled locally, not forwarded) ───
const INSTANCE_TOOLS = [
  {
    name: "unity_list_instances",
    description:
      "List all running Unity editors that expose the Adanub MCP bridge (scans ports " +
      "7890-7899). Use this when more than one editor is open to choose which to target.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "unity_select_instance",
    description:
      "Select which Unity editor subsequent tool calls target, by port. Get the port " +
      "from unity_list_instances. The selection persists until changed.",
    inputSchema: {
      type: "object",
      properties: {
        port: { type: "number", description: "Target editor's bridge port (e.g. 7890)." },
      },
      required: ["port"],
    },
  },
];

// Optional per-call routing override injected into every forwarding tool's schema.
const PORT_PROP = {
  port: {
    type: "number",
    description:
      "Optional: target a specific Unity editor by bridge port (from unity_list_instances). " +
      "Overrides the current selection — use when multiple editors are open.",
  },
};

// ─── CLI: emit the read-only tool permission names (for the bootstrap allowlist) ───
// `node src/index.js --list-readonly-tools` prints every non-mutating tool as a
// Claude Code permission string, so the install can keep .claude/settings.json in
// sync automatically instead of hand-maintaining the list.
if (process.argv.includes("--list-readonly-tools")) {
  const names = [
    ...INSTANCE_TOOLS.map((t) => t.name),
    ...TOOLS.filter((t) => !t.mutates).map((t) => t.name),
  ];
  for (const n of names) process.stdout.write(`mcp__adanub-unity-mcp__${n}\n`);
  process.exit(0);
}

const server = new Server(
  { name: "adanub-unity-mcp", version: "0.2.0" },
  { capabilities: { tools: {} } }
);

server.setRequestHandler(ListToolsRequestSchema, async () => ({
  tools: [
    ...INSTANCE_TOOLS,
    ...TOOLS.map(({ name, description, inputSchema }) => ({
      name,
      description,
      inputSchema: {
        ...inputSchema,
        properties: { ...(inputSchema.properties || {}), ...PORT_PROP },
      },
    })),
  ],
}));

const text = (s) => ({ content: [{ type: "text", text: s }] });
const errorText = (s) => ({ content: [{ type: "text", text: s }], isError: true });

server.setRequestHandler(CallToolRequestSchema, async (request) => {
  const { name, arguments: args } = request.params;

  // ── Instance management ──
  if (name === "unity_list_instances") {
    const instances = await discoverInstances();
    if (instances.length === 0) {
      return text("No Unity editors with the Adanub MCP bridge are running (scanned 7890-7899).");
    }
    return text(
      `Found ${instances.length} Unity editor(s):\n${describeInstances(instances)}\n\n` +
        `Call unity_select_instance with a port to target one.`
    );
  }

  if (name === "unity_select_instance") {
    const port = args?.port;
    if (!port) return errorText("unity_select_instance requires a 'port'.");
    const res = await selectInstance(port);
    if (res.error) return errorText(res.error);
    const s = res.selected;
    return text(`Selected ${s.projectName} on port ${s.port} (Unity ${s.unityVersion}).\n${s.projectPath}`);
  }

  // ── Forwarding tools ──
  const tool = TOOLS_BY_NAME.get(name);
  if (!tool) return errorText(`Unknown tool: ${name}`);

  const { port: explicitPort, ...routeArgs } = args ?? {};
  const target = await resolveTargetPort(explicitPort);

  if (target.error) return errorText(target.error);
  if (target.needsSelection) {
    return errorText(
      `Multiple Unity editors are open — select one before using this tool:\n` +
        `${describeInstances(target.needsSelection)}\n\n` +
        `Call unity_select_instance with the desired port (or pass port:<n> on the call).`
    );
  }

  try {
    const result = await callWithRecovery(tool.route, routeArgs, target);
    return text(JSON.stringify(result, null, 2));
  } catch (err) {
    return errorText(`Error: ${err.message}`);
  }
});

async function main() {
  const transport = new StdioServerTransport();
  await server.connect(transport);
  console.error("[Adanub Unity MCP] stdio server running");
}

main().catch((err) => {
  console.error("[Adanub Unity MCP] Fatal:", err);
  process.exit(1);
});
