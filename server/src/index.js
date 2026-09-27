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
  resolveProjectIdentity,
  describeInstances,
  callUnity,
  InstanceSelectionRequired,
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
        maxDepth: { type: "number", description: "Hierarchy depth to walk (default 3). Ignored when 'match' is given." },
        match: { type: "string", description: "Find samples by name ANYWHERE in the tree (case-insensitive substring, or regex with 'regex'); each hit reports its ancestor path. The way to read a custom ProfilerMarker buried in the render loop." },
        regex: { type: "boolean", description: "Treat 'match' as a regex (default false)." },
      },
    },
    route: "profiler/frame-data",
  },
  {
    name: "unity_profiler_record",
    description:
      "Start or stop Profiler recording (ProfilerDriver.enabled) so frame data can be read without the " +
      "user touching the Profiler window. Optionally clears captured frames first or toggles Deep Profile " +
      "(which triggers a script recompile). Returns the resulting state and frame range.",
    inputSchema: {
      type: "object",
      properties: {
        enabled: { type: "boolean", description: "true = record, false = stop." },
        clear: { type: "boolean", description: "Drop the captured frames before applying (default false)." },
        deepProfiling: { type: "boolean", description: "Set Deep Profile on/off. Changing it makes Unity recompile scripts." },
      },
      required: ["enabled"],
    },
    route: "profiler/record",
    mutates: true,
  },
  {
    name: "unity_profiler_analyze",
    description: "Combined snapshot: memory + (Play-mode) rendering + (if recording) CPU hotspots + scene complexity + suggestions.",
    inputSchema: { type: "object", properties: {} },
    route: "profiler/analyze",
  },

  // ── Frame Debugger ──
  {
    name: "unity_framedebugger_enable",
    description:
      "Enable the editor's Frame Debugger on the Game view (shows the Game view tab; pauses play mode if playing) and wait " +
      "until the frame's events are in. Returns eventCount and eventsHash. Then read unity_framedebugger_events; call " +
      "unity_framedebugger_disable when done (it unpauses if enable paused). PRECONDITION: the Game view renders only while " +
      "the editor application has OS focus — eventCount 0 after the wait means the editor is in the background, not a fault; " +
      "ask the user to click the editor, then read events (the debugger stays enabled). This tool never takes focus.",
    inputSchema: {
      type: "object",
      properties: {
        waitMs: { type: "number", description: "How long to wait for the event list to settle (default 5000, max 25000)." },
      },
    },
    route: "framedebugger/enable",
    mutates: true,
  },
  {
    name: "unity_framedebugger_events",
    description:
      "Events of the frame the Frame Debugger holds: index, type (SRPBatch, Mesh, SkinOnGPU, InstancedMesh, ...), profiler-marker " +
      "path and drawn object name. A draw inside an SRPBatch event was SRP-batched; a Mesh/SkinOnGPU event was not. Use " +
      "summary=true for counts per marker path + type (thousands of draws come back as dozens of rows).",
    inputSchema: {
      type: "object",
      properties: {
        summary: { type: "boolean", description: "Counts per (marker path, type) instead of rows (default false)." },
        match: { type: "string", description: "Only events whose marker path or object name contains this (or matches, with regex)." },
        regex: { type: "boolean", description: "Treat 'match' as a case-insensitive regex (default false)." },
        type: { type: "string", description: "Only events of this type name (e.g. Mesh, SRPBatch, SkinOnGPU)." },
        offset: { type: "number", description: "Skip this many matched rows (default 0)." },
        maxItems: { type: "number", description: "Max rows (default 200)." },
      },
    },
    route: "framedebugger/events",
  },
  {
    name: "unity_framedebugger_event_data",
    description:
      "Detail for chosen Frame Debugger events: shader, pass, light mode, keywords, mesh, index/instance/draw counts, the BATCH " +
      "BREAK CAUSE (why the draw did not join the previous batch), render target and depth/raster/blend state. The editor holds " +
      "this for one event at a time and needs a Game view re-render per event, so pass a short list (max 32). Restores the " +
      "draw-call limit afterwards. Same precondition as enable: the re-render happens only while the editor application has " +
      "OS focus; an index reported as 'no data' is the editor in the background.",
    inputSchema: {
      type: "object",
      properties: {
        indices: { type: "array", items: { type: "number" }, description: "Event indices from unity_framedebugger_events (max 32)." },
        waitMs: { type: "number", description: "Wait per event for the re-render (default 2000, max 10000)." },
      },
      required: ["indices"],
    },
    route: "framedebugger/event-data",
    mutates: true,
  },
  {
    name: "unity_framedebugger_disable",
    description: "Disable the Frame Debugger; unpauses play mode if unity_framedebugger_enable paused it.",
    inputSchema: { type: "object", properties: {} },
    route: "framedebugger/disable",
    mutates: true,
  },

  // ── Windows and editor control ──
  {
    name: "unity_window_show",
    description:
      "Show an editor window by type name (GameView, SceneView, ProfilerWindow, FrameDebuggerWindow, or a project window type), " +
      "opening it if needed. focus=false (default) brings the tab forward without taking keyboard focus. Editor tab focus only; " +
      "never brings the editor process to the foreground.",
    inputSchema: {
      type: "object",
      properties: {
        type: { type: "string", description: "Window type name or full name." },
        focus: { type: "boolean", description: "Also take keyboard focus (default false)." },
      },
      required: ["type"],
    },
    route: "window/show",
    mutates: true,
  },
  {
    name: "unity_window_close",
    description:
      "Close an open editor window by type name — the counterpart of unity_window_show, so a window opened for a measurement " +
      "is not left in the user's layout. Closes every open instance of that type.",
    inputSchema: {
      type: "object",
      properties: {
        type: { type: "string", description: "Window type name or full name." },
      },
      required: ["type"],
    },
    route: "window/close",
    mutates: true,
  },
  {
    name: "unity_gameview_info",
    description:
      "Game view render settings — the like-for-like conditions of a measurement: target render size, selected size entry, " +
      "VSync toggle, low-resolution-aspect mode, target display, window size; plus QualitySettings.vSyncCount and Screen size " +
      "while playing.",
    inputSchema: { type: "object", properties: {} },
    route: "gameview/info",
  },
  {
    name: "unity_editor_playmode",
    description:
      "Play-mode control: play (enter), stop (exit), pause, unpause, step (one frame while paused). play and stop return " +
      "only once the transition has COMPLETED — the call waits on the editor's playModeStateChanged event (no polling), " +
      "rides out a domain reload, and reports settled:false with a note when a play entry was refused (a pre-play " +
      "validation, compile errors) or the transition did not complete within its bound.",
    inputSchema: {
      type: "object",
      properties: {
        action: { type: "string", enum: ["play", "stop", "pause", "unpause", "step"], description: "What to do." },
      },
      required: ["action"],
    },
    route: "editor/playmode",
    mutates: true,
  },
  {
    name: "unity_scene_open",
    description:
      "Open a scene asset in edit mode ('single' replaces the open scenes, 'additive' adds). Refused, with the scenes named, when " +
      "a loaded scene has unsaved changes that 'single' would discard — it never saves or discards for the user.",
    inputSchema: {
      type: "object",
      properties: {
        path: { type: "string", description: "Scene asset path, e.g. Assets/Scenes/Foo.unity." },
        mode: { type: "string", enum: ["single", "additive"], description: "Default single." },
      },
      required: ["path"],
    },
    route: "scene/open",
    mutates: true,
  },
  {
    name: "unity_editor_menu_item",
    description:
      "Execute an editor menu item by menu path (e.g. 'Assets/Reimport All'). Returns whether the editor found and ran it. " +
      "An item that opens a dialog leaves the dialog for the user.",
    inputSchema: {
      type: "object",
      properties: {
        path: { type: "string", description: "Menu path, case-sensitive, '/'-separated." },
      },
      required: ["path"],
    },
    route: "editor/menu-item",
    mutates: true,
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

  // ── Memory snapshots ──
  {
    name: "unity_memory_snapshot_start",
    description:
      "Start a memory snapshot of this editor into <project>/MemoryCaptures and return its path at once (low level; " +
      "unity_memory_snapshot captures and waits in one call). A name whose file exists or is being captured returns that " +
      "capture instead of starting another. Default flags NativeObjects + NativeAllocations (about 45 MB for an editor, " +
      "everything unity_memory_snapshot_diff reads); add ManagedObjects, NativeAllocationSites and NativeStackTraces for " +
      "the Memory Profiler window's full set.",
    inputSchema: {
      type: "object",
      properties: {
        name: { type: "string", description: "File name without extension (default <product>_<timestamp>)." },
        folder: { type: "string", description: "Absolute or project-relative folder (default MemoryCaptures)." },
        flags: { type: "array", items: { type: "string" }, description: "CaptureFlags names (default NativeObjects, NativeAllocations)." },
        collectGarbage: { type: "boolean", description: "Collect managed garbage before capturing (default true)." },
      },
    },
    route: "memory/snapshot",
    mutates: true,
  },
  {
    name: "unity_memory_snapshot_status",
    description:
      "Whether the snapshot at a path is a finished capture: state missing | incomplete | complete | invalid, plus " +
      "finished and failed. Reads only the file, so it answers across a domain reload.",
    inputSchema: {
      type: "object",
      properties: {
        path: { type: "string", description: "The capture's path (from unity_memory_snapshot or _start)." },
        waitMs: { type: "number", description: "Long-poll up to this many ms for finished (0-25000, default 0)." },
      },
      required: ["path"],
    },
    route: "memory/snapshot-status",
  },
  {
    name: "unity_memory_snapshot_diff",
    description:
      "Compare two finished snapshots of this process: totals; native objects by type, with the new ones named; native " +
      "allocations and GPU resources by owning root ('area: object', or (unrooted)), with the new GPU resources listed; " +
      "allocators by used size. Each table lists only what changed, largest first, cut to limit rows, and says how many " +
      "changed. Take the 'after' capture a few frames after releasing anything: D3D12 frees a disposed GPU buffer late " +
      "(it sits under 'Rendering: D3D12GfxDevice' until then). Compare owners, not IDs, across a domain reload.",
    inputSchema: {
      type: "object",
      properties: {
        before: { type: "string", description: "Path of the earlier capture." },
        after: { type: "string", description: "Path of the later capture." },
        limit: { type: "number", description: "Rows per table (default 20, max 500)." },
      },
      required: ["before", "after"],
    },
    route: "memory/snapshot-diff",
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
    description:
      "Set the editor selection to scene objects and/or project assets (changes editor state). Selecting an asset makes the Inspector show it, which is the usual setup step before unity_uitk_dump. Reports anything that failed to resolve, and warns when an Inspector is locked and so will not follow the selection.",
    inputSchema: {
      type: "object",
      properties: {
        paths: { type: "array", items: { type: "string" }, description: "Scene GameObject hierarchy paths to select." },
        instanceIds: { type: "array", items: { type: "number" }, description: "Instance ids to select." },
        assetPaths: {
          type: "array",
          items: { type: "string" },
          description: "Project asset paths to select, e.g. 'Assets/Data/Thing.asset'.",
        },
        guids: { type: "array", items: { type: "string" }, description: "Asset GUIDs to select." },
        ping: { type: "boolean", description: "Also highlight the first selected object in the Project window." },
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

  // ── UI Toolkit (editor UI) ──
  {
    name: "unity_uitk_windows",
    description:
      "Open EditorWindows, their UI Toolkit root child counts, and whether each is locked/focused. Use to find the window type name for unity_uitk_dump.",
    inputSchema: { type: "object", properties: {} },
    route: "uitk/windows",
  },
  {
    name: "unity_uitk_repaint",
    description:
      "Ask editor windows to redraw so their retained UI Toolkit trees rebuild. Call after unity_selection_set, then unity_uitk_dump on a FOLLOWING call — an unfocused editor does not redraw on its own, so a dump taken without this reports the previous selection's tree (or an empty one), which is indistinguishable from a real finding.",
    inputSchema: {
      type: "object",
      properties: {
        window: { type: "string", description: "EditorWindow type name. Default: all windows." },
      },
    },
    route: "uitk/repaint",
  },
  {
    name: "unity_uitk_expand_inspector",
    description:
      "Expand or collapse the Inspector's component foldouts for the current selection so their contents are actually built. A collapsed component builds no inspector content, so dumping it reports nothing — indistinguishable from a component whose fields failed to draw. Use before unity_uitk_dump when inspecting components on a GameObject or prefab.",
    inputSchema: {
      type: "object",
      properties: {
        expanded: { type: "boolean", description: "Expand (default true) or collapse." },
        types: { type: "array", items: { type: "string" }, description: "Component type names to affect. Default: all on the selection." },
      },
    },
    route: "uitk/expand-inspector",
    mutates: true,
  },
  {
    name: "unity_uitk_set_foldout",
    description:
      "Expand or collapse Foldouts INSIDE a window's visual tree — a group, a list, a nested block. unity_uitk_expand_inspector cannot reach these; it toggles component headers only. Needed because a collapsed subtree reports zero geometry and zeroed margins (the numbers come from a layout that never ran), so nothing about expanded content is measurable until something opens it. Reports which foldouts it touched, and says so explicitly when none matched.",
    inputSchema: {
      type: "object",
      properties: {
        window: {
          type: "string",
          description: "EditorWindow type name, e.g. 'InspectorWindow'. Default: the focused window. '*' for all.",
        },
        selector: {
          type: "string",
          description: "'.uss-class' or a TypeName to limit the search to matching subtrees. Default: the whole window.",
        },
        text: {
          type: "string",
          description: "Case-insensitive substring of the foldout's header text, to target one by name.",
        },
        expanded: { type: "boolean", description: "Expand (default true) or collapse." },
        limit: { type: "number", description: "Max foldouts to touch (default 50); reports truncation." },
      },
    },
    route: "uitk/set-foldout",
    mutates: true,
  },
  {
    name: "unity_uitk_dump",
    description:
      "Dump an editor window's UI Toolkit visual tree as numbers: per element the type, full class list, geometry, box metrics, display/visibility/opacity, resolved AND inline colours, text/label, and background image. The tool for editor-UI work where a screenshot cannot answer the question — whether an element owns a property inline or a stylesheet still drives it, what a zero-height element's box computed to, or which unexpected element is actually painting. Enumerates every child (never a class whitelist) and reports truncation explicitly.",
    inputSchema: {
      type: "object",
      properties: {
        window: {
          type: "string",
          description: "EditorWindow type name, e.g. 'InspectorWindow'. Default: the focused window. '*' for all.",
        },
        selector: {
          type: "string",
          description: "Root the dump at matching elements: '.uss-class' by class, otherwise an element type name. Every match is dumped.",
        },
        properties: {
          type: "array",
          items: { type: "string", enum: ["geometry", "box", "display", "colour", "text", "image"] },
          description: "Property groups to report. Default: geometry, box, display, text. Add 'colour' for tint/stylesheet-ownership questions, 'image' for icons.",
        },
        maxDepth: { type: "number", description: "Max tree depth (default 12). Truncation is reported." },
        maxElements: { type: "number", description: "Max elements emitted (default 400). Truncation is reported." },
        includeHidden: { type: "boolean", description: "Include display:None elements (default true — a hidden element is often the finding)." },
      },
    },
    route: "uitk/dump",
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
      "Select which Unity editor subsequent tool calls target, by project or by port. Prefer " +
      "'project' (a substring of the project path or name) — ports shuffle between editors " +
      "across restarts and domain reloads, so a port remembered from earlier can silently " +
      "point at a different editor. The selection persists until changed and follows the " +
      "project if its port moves.",
    inputSchema: {
      type: "object",
      properties: {
        project: {
          type: "string",
          description:
            "Target editor by project identity: a case-insensitive substring of its project " +
            "path or name (from unity_list_instances). Must match exactly one running editor.",
        },
        port: { type: "number", description: "Target editor's bridge port (e.g. 7890)." },
      },
    },
  },
];

// Optional per-call routing overrides injected into every forwarding tool's schema.
const PORT_PROP = {
  port: {
    type: "number",
    description:
      "Optional: target a specific Unity editor by bridge port (from unity_list_instances). " +
      "Overrides the current selection. Ports shuffle between editors across restarts and " +
      "domain reloads — when editors of different projects are open, prefer 'project'.",
  },
};
const PROJECT_PROP = {
  project: {
    type: "string",
    description:
      "Optional: target a specific Unity editor by project identity — a case-insensitive " +
      "substring of its project path or name (from unity_list_instances). Overrides the " +
      "current selection and stays correct when domain reloads shuffle ports. Mutually " +
      "exclusive with 'port'.",
  },
};

// ─── Combined compile tool (locally orchestrated: request → poll → result) ───
// One call does the whole edit-verify loop, so callers don't hand-orchestrate compile_request +
// repeated compile_status (+ instance re-listing). Auto-selects the instance, triggers the
// compile, and polls until it finishes — transparently outlasting the domain-reload bridge drop
// (callUnity re-resolves the moved port) and re-polling past the empty mid-reload responses.
// The request call's resolved project identity is pinned for every status poll, so the
// request/status pair can never split across editors mid-reload.
const COMPILE_TOOL = {
  name: "unity_compile",
  description:
    "Compile edited scripts in ONE call: triggers AssetDatabase.Refresh + compile, waits for it to " +
    "finish (transparently handling the domain-reload bridge drop and re-polling past transient empty " +
    "responses), and returns the result (clean | errors | noCompile) with compiler messages. Prefer this " +
    "after editing .cs files instead of the separate unity_compile_request + unity_compile_status loop. " +
    "Works without focusing the editor. Caveat: in play mode the editor may defer compiles until play " +
    "exits, so noCompile + isPlaying:true is inconclusive.",
  inputSchema: {
    type: "object",
    properties: {
      count: { type: "number", description: "Max compiler messages returned (default 50)." },
      port: PORT_PROP.port,
      project: PROJECT_PROP.project,
    },
  },
  mutates: true,
};

// Each poll long-polls the bridge up to 25s for phase=finished; this caps the total wait so a stuck
// or never-finishing compile can't hang the call indefinitely (8 × 25s ≈ 200s worst case).
const COMPILE_MAX_POLLS = 8;

// ─── Combined snapshot capture (locally orchestrated: start → status until finished) ───
// One call captures and waits. A name is always sent — generated here when the caller gives none — so a
// start retried through a domain reload's bridge outage returns the same capture instead of starting a
// second, and the path is known even when the start itself timed out on the main thread.
const SNAPSHOT_TOOL = {
  name: "unity_memory_snapshot",
  description:
    "Capture a memory snapshot of this editor into <project>/MemoryCaptures and wait until the file is a finished " +
    "capture; returns its path and state. Default flags NativeObjects + NativeAllocations (about 45 MB for an editor, " +
    "everything unity_memory_snapshot_diff reads); pass flags with ManagedObjects, NativeAllocationSites and " +
    "NativeStackTraces added for the Memory Profiler window's full set. For a leak check, take the 'after' capture a " +
    "few frames after releasing anything: D3D12 frees a disposed GPU buffer late.",
  inputSchema: {
    type: "object",
    properties: {
      name: {
        type: "string",
        description: "File name without extension (default snapshot_<local timestamp>); an existing or running capture of that name is returned instead of a new one.",
      },
      folder: { type: "string", description: "Absolute or project-relative folder (default MemoryCaptures)." },
      flags: { type: "array", items: { type: "string" }, description: "CaptureFlags names (default NativeObjects, NativeAllocations)." },
      collectGarbage: { type: "boolean", description: "Collect managed garbage before capturing (default true)." },
      port: PORT_PROP.port,
      project: PROJECT_PROP.project,
    },
  },
  mutates: true,
};

// Each status poll long-polls up to 25 s. A capture writes its file inside the start call, so the first poll
// normally answers at once; the cap keeps a stuck capture from hanging the call.
const SNAPSHOT_MAX_POLLS = 4;

function snapshotName() {
  const d = new Date();
  const p = (n) => String(n).padStart(2, "0");
  return `snapshot_${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}_${p(d.getHours())}-${p(d.getMinutes())}-${p(d.getSeconds())}`;
}

async function waitForSnapshot(path, opts) {
  let last;
  for (let i = 0; i < SNAPSHOT_MAX_POLLS; i++) {
    ({ result: last } = await callUnity("memory/snapshot-status", { path, waitMs: 25000 }, opts));
    if (last && (last.finished || last.error)) return last;
  }
  return { ...(last || {}), note: "The capture did not report finished within the poll budget; poll unity_memory_snapshot_status with this path." };
}

// Each wait blocks on the editor's play-mode event for up to 25 s; a domain reload drops the request and
// callUnity re-resolves the editor, so the next wait answers from the reloaded state. This caps the total
// (4 × 25 s) so a transition that never completes cannot hang the call.
const PLAYMODE_MAX_WAITS = 4;

async function waitForPlayMode(target, editEntries, opts) {
  let last;
  for (let i = 0; i < PLAYMODE_MAX_WAITS; i++) {
    ({ result: last } = await callUnity("editor/playmode-wait", { target, editEntries, waitMs: 25000 }, opts));
    if (last && (last.settled || last.error || last.note?.startsWith("The play entry ended"))) return last;
  }
  return { ...(last || {}), note: "The play-mode transition did not complete within the wait budget." };
}

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
  { name: "adanub-unity-mcp", version: "0.4.0" },
  { capabilities: { tools: {} } }
);

server.setRequestHandler(ListToolsRequestSchema, async () => ({
  tools: [
    ...INSTANCE_TOOLS,
    COMPILE_TOOL,
    SNAPSHOT_TOOL,
    ...TOOLS.map(({ name, description, inputSchema }) => ({
      name,
      description,
      inputSchema: {
        ...inputSchema,
        properties: { ...(inputSchema.properties || {}), ...PORT_PROP, ...PROJECT_PROP },
      },
    })),
  ],
}));

const text = (s) => ({ content: [{ type: "text", text: s }] });
const errorText = (s) => ({ content: [{ type: "text", text: s }], isError: true });

// Single owner of the callUnity failure → MCP response mapping, shared by every call site.
const toolFailureText = (err) =>
  err instanceof InstanceSelectionRequired
    ? errorText(
        `Multiple Unity editors are open — select one before using this tool:\n` +
          `${describeInstances(err.instances)}\n\n` +
          `Call unity_select_instance (prefer project:<substring>; ports shuffle across reloads), ` +
          `or pass project:<substring> on the call.`
      )
    : errorText(`Error: ${err.message}`);

// Per-call routing: a 'project' override resolves to the instance's current port PLUS its
// project identity, so callUnity treats it like an explicit port (never mutating the saved
// selection) while still following the project if a domain reload moves it to another port.
async function resolveRouting(explicitPort, project) {
  if (!project) return { explicitPort };
  if (explicitPort) throw new Error("Pass either 'project' or 'port', not both.");
  const resolved = await resolveProjectIdentity(project);
  if (resolved.error) throw new Error(resolved.error);
  return { explicitPort: resolved.instance.port, pinnedPath: resolved.instance.projectPath };
}

// Poll compile/status until it reports phase=finished. callUnity rides out the domain-reload
// bridge drop (re-resolving the moved port); this loop additionally re-polls past the empty/non-finished
// responses the bridge can return mid-reload, so a single unity_compile call resolves to the result.
async function waitForCompile(opts, count) {
  let last;
  for (let i = 0; i < COMPILE_MAX_POLLS; i++) {
    ({ result: last } = await callUnity("compile/status", { waitMs: 25000, count }, opts));
    if (last && last.phase === "finished") return last;
  }
  return { ...(last || {}), note: "Compile did not report 'finished' within the poll budget." };
}

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
    const { port, project } = args ?? {};
    if (!port && !project) return errorText("unity_select_instance requires a 'project' or a 'port'.");
    if (port && project) return errorText("Pass either 'project' or 'port', not both.");
    let targetPort = port;
    if (project) {
      const resolved = await resolveProjectIdentity(project);
      if (resolved.error) return errorText(resolved.error);
      targetPort = resolved.instance.port;
    }
    const res = await selectInstance(targetPort);
    if (res.error) return errorText(res.error);
    const s = res.selected;
    return text(`Selected ${s.projectName} on port ${s.port} (Unity ${s.unityVersion}).\n${s.projectPath}`);
  }

  // ── Combined compile (locally orchestrated: request → poll → result) ──
  if (name === "unity_compile") {
    const { port: explicitPort, project, count } = args ?? {};
    try {
      const routing = await resolveRouting(explicitPort, project);
      const { projectPath } = await callUnity("compile/request", {}, routing);
      const status = await waitForCompile({ ...routing, pinnedPath: projectPath || routing.pinnedPath }, count);
      return text(JSON.stringify(status, null, 2));
    } catch (err) {
      return toolFailureText(err);
    }
  }

  // ── Play-mode control (locally orchestrated: request → wait on the editor's play-mode event) ──
  if (name === "unity_editor_playmode") {
    const { port: explicitPort, project, action } = args ?? {};
    try {
      const routing = await resolveRouting(explicitPort, project);
      const { result: requested, projectPath } = await callUnity("editor/playmode", { action }, routing);
      const target = action === "play" ? "play" : action === "stop" ? "edit" : null;
      if (!target || requested?.error || requested?.note) return text(JSON.stringify(requested, null, 2));
      const settled = await waitForPlayMode(target, target === "play" ? requested.editEntries : undefined, {
        ...routing,
        pinnedPath: projectPath || routing.pinnedPath,
      });
      return text(JSON.stringify({ action, ...settled }, null, 2));
    } catch (err) {
      return toolFailureText(err);
    }
  }

  // ── Combined snapshot capture (locally orchestrated: start → status until finished) ──
  if (name === "unity_memory_snapshot") {
    const { port: explicitPort, project, ...captureArgs } = args ?? {};
    try {
      const routing = await resolveRouting(explicitPort, project);
      const request = { ...captureArgs, name: captureArgs.name || snapshotName() };
      const { result: started, projectPath } = await callUnity("memory/snapshot", request, routing);
      // A refused start (a bad name, a directory at the path) is final; a main-thread timeout may still be capturing.
      const timedOut = typeof started?.error === "string" && /Timed out/.test(started.error);
      if (started?.error && !timedOut) return errorText(`Error: ${started.error}`);
      const path = started?.path || (!request.folder && projectPath ? `${projectPath}/MemoryCaptures/${request.name}.snap` : null);
      if (!path) return errorText(`Error: ${started?.error ?? "the capture did not start"}`);
      const status = await waitForSnapshot(path, { ...routing, pinnedPath: projectPath || routing.pinnedPath });
      return text(
        JSON.stringify(
          { started: started?.started ?? null, startError: started?.error, flags: started?.flags, editorApplicationActive: started?.editorApplicationActive, ...status },
          null,
          2
        )
      );
    } catch (err) {
      return toolFailureText(err);
    }
  }

  // ── Forwarding tools ──
  const tool = TOOLS_BY_NAME.get(name);
  if (!tool) return errorText(`Unknown tool: ${name}`);

  const { port: explicitPort, project, ...routeArgs } = args ?? {};
  try {
    const routing = await resolveRouting(explicitPort, project);
    const { result } = await callUnity(tool.route, routeArgs, routing);
    return text(JSON.stringify(result, null, 2));
  } catch (err) {
    return toolFailureText(err);
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
