# agentic-unity-tooling

An MCP toolset for **observing and inspecting** a running Unity Editor from an AI agent —
console logs, compilation errors, profiler/memory data, memory snapshots and their differences, and
scene/asset/prefab/project state — plus a few deliberate write paths, chiefly **triggering an asset
refresh + script compile**, so an agent that edits scripts on disk gets compiler feedback without a
human having to focus the editor.
Read-focused by design (not an "AI builds your scene" tool). Project-agnostic and reusable.

Designed to be automatically setup by Claude Code with minimal user intervention needed.

Package id `com.adanub.unity-mcp`. MIT-licensed; see "Licence" for the one part written from another
package's source.

## Architecture

```
Claude ──stdio──▶ server/ (Node MCP shim) ──HTTP 127.0.0.1:789x──▶ plugin/ (C# bridge inside Unity)
```

Unity Editor code can't itself be an MCP stdio process, so the tool is split:

| Part      | What it is                                                                            |
|-----------|---------------------------------------------------------------------------------------|
| `plugin/` | Unity UPM package (`com.adanub.unity-mcp`). An `HttpListener` bridge + reflection-dispatched route handlers, Editor-only. Routes self-register via `[McpRoute("...")]` — adding a tool is a self-contained command class, no central switch. |
| `server/` | Node MCP stdio server. Exposes `unity_*` tools that forward to the bridge.            |

The bridge marshals calls onto Unity's main thread (routes can opt into running on the request
thread instead — used for long-polling, e.g. `compile/status`), survives domain reloads, and binds
the first free port in **7890–7899** so multiple editors (e.g. game client + server) run side by
side. The Node server retries through a domain reload's bridge outage with backoff, re-locating
the editor by project path if the reload moved it to a different port.

## Install

Designed to be installed by a thin per-project skill that does this idempotently (clone-or-reuse a
single shared clone, junction the plugin into `Packages/`, register the MCP server, generate the
read-only allowlist). Manual equivalent on Windows, from a Unity project root:

```powershell
# 1. clone once (reuse across projects)
git clone https://github.com/adanub/agentic-unity-tooling.git <somewhere>/agentic-unity-tooling

# 2. make Unity compile the plugin (embedded package via junction)
cmd /c mklink /J "Packages\com.adanub.unity-mcp" "<somewhere>\agentic-unity-tooling\plugin"

# 3. server deps + point your MCP client at it
npm --prefix "<somewhere>\agentic-unity-tooling\server" install
#    .mcp.json:  { "mcpServers": { "adanub-unity-mcp": { "command": "node",
#                  "args": ["<somewhere>/agentic-unity-tooling/server/src/index.js"] } } }

# 4. (optional) emit the read-only tool names for your allowlist
node "<somewhere>/agentic-unity-tooling/server/src/index.js" --list-readonly-tools
```

Then restart the MCP client and focus the Unity editor so it compiles the package; the bridge logs
`[Adanub MCP] Bridge started on http://127.0.0.1:789x/`.

## Tools

- **Observe**: console log (collapses per-frame spam with counts; reads Unity's own Console store),
  compilation errors (survive domain reload), editor/project/scene state, profiler stats/memory/
  frame-data/analyze, asset memory breakdown + top consumers. `unity_profiler_frame_data` walks the
  CPU hierarchy to a chosen `maxDepth`, or with `match` finds samples by name anywhere in the tree
  (each hit with its ancestor path) — how a custom `ProfilerMarker` deep in the render loop is read.
  `unity_profiler_record` starts/stops recording (and can clear frames or toggle Deep Profile), so a
  measurement needs no hand on the Profiler window.
- **Frame Debugger**: `unity_framedebugger_enable` enables the editor's Frame Debugger on the Game
  view (pausing play mode if playing) and waits for the frame's events; `unity_framedebugger_events`
  lists them — type (`SRPBatch`, `Mesh`, `SkinOnGPU`, `InstancedMesh`, ...), profiler-marker path,
  drawn object — or, with `summary`, counts per marker path and type, so thousands of draws come
  back as dozens of rows; `unity_framedebugger_event_data` reads the detail of a short list of
  events (shader, pass, mesh, counts, render target, depth/raster/blend state and the **batch-break
  cause** — why a draw did not join the previous batch); `unity_framedebugger_disable` undoes it.
  Two preconditions the routes report rather than work around: the Game view renders **only while
  the editor application has OS focus** (no bridge-side repaint request substitutes for it, and the
  bridge never takes focus), and per-event detail exists for one event at a time, populated by a Game
  view re-render at that event, so detail is sampled, never bulk. Do not open the editor's own Frame
  Debugger window while the bridge has the debugger enabled — the window expects to enable it itself
  and throws per repaint until it is closed.
- **Editor control**: `unity_window_show` / `unity_window_close` (a window by type name; `focus` is
  editor-tab focus only), `unity_gameview_info` (render size, selected size, VSync, target display —
  the like-for-like conditions of a measurement), `unity_editor_playmode` (play / stop / pause /
  unpause / step; play and stop return once the transition has completed — the server waits on the
  `editor/playmode-wait` route, which blocks on the editor's `playModeStateChanged` event rather than
  polling, and rides out a domain reload; a play entry is refused at once while scripts have compile
  errors, reported as `ended: "edit"` as soon as the editor returns to edit mode, and otherwise
  reported unsettled after one 25 s wait when no transition started), `unity_scene_open` (refused while a loaded scene has unsaved changes it would
  discard; never saves or discards for the user) and `unity_editor_menu_item` (a menu path; a
  dialog it opens is left for the user). All but the two reads are `mutates: true`.
- **Compile**: `unity_compile_request` triggers `AssetDatabase.Refresh()` so the editor picks up
  script edits made on disk — deferred onto `EditorApplication.update` (NOT `delayCall`, which an
  unfocused editor can defer indefinitely), so it works with the editor in the background.
  `unity_compile_status` long-polls the session until `finished` with result
  `clean | errors | noCompile` plus the compiler messages. The session survives the clean-compile
  domain reload via `SessionState`; on errors there is no reload and results are immediate.
- **Memory snapshots**: `unity_memory_snapshot` captures the editor (`MemoryProfiler.TakeSnapshot`)
  into `<project>/MemoryCaptures` and waits until the file is a finished capture;
  `unity_memory_snapshot_diff` compares two captures — totals, native objects by type with the new ones
  named, native allocations and GPU resources by owning root, allocators — each table largest change
  first and cut to a stated limit; `unity_memory_snapshot_start` / `unity_memory_snapshot_status` are
  the low-level pair. The default flags (native objects and allocations) hold everything the diff reads
  in about 45 MB for an editor, where the Memory Profiler window's full set runs to about 1.5 GB. The
  file is a capture's only record, so a capture survives a domain reload with no job state. What the
  tools are built around, each measured: `TakeSnapshot` captures inside the call and runs its finish
  callback before returning, and a capture started inside that callback never completes and cancels
  every later one until a domain reload; D3D12 keeps a disposed GPU buffer (under the root
  `Rendering: D3D12GfxDevice`) until the GPU is done with it, so take the "after" capture a few frames
  after a release; IDs change when a resource is recreated, so compare owners, not IDs, across a
  domain reload. A GraphicsBuffer that was never bound still shows, under `Rendering: GraphicsBuffers`;
  an editor capture records no per-allocator allocation counts. The reader and the diff
  (`plugin/Editor/Snapshot/`) are engine-free and tested headless:
  `dotnet run --project tests/SnapshotReader.Tests -- <capture.snap>`.
- **Inspect**: scene hierarchy (bounded), search by name/component/tag/layer/shader, asset search,
  missing references, selection, GameObject + component properties, prefab info/hierarchy/
  variant overrides.
- **Assets/graphics**: asset list, script read, ScriptableObject props + type list, shader list +
  properties, mesh/material/texture/renderer info, lighting summary, texture import settings.
- **Project config**: tags/layers, physics collision matrix, assembly definitions, sprite atlases,
  input actions, packages, scene-view camera, editor/player prefs.
- **Editor UI (UI Toolkit)**: `unity_uitk_windows` lists open `EditorWindow`s with their UI Toolkit
  roots, lock and focus state; `unity_uitk_dump` dumps a window's visual tree as numbers — per
  element the type, full class list, geometry, box metrics, display/visibility/opacity, resolved
  **and inline** colours, text/label, and background image. This is the counterpart to a screenshot
  for editor-UI work: a screenshot cannot show whether an element owns a property inline or a
  stylesheet still drives it, what a zero-height element's box computed to, or that the element
  painting the wrong thing is one nobody thought to look at. It walks the **hierarchy** — so a
  composite control's own chrome (a foldout's toggle, a field's label) is visible, which the public
  `Children()` enumeration hides — enumerates every child rather than a whitelist of expected
  classes, states plainly when it truncated, and **annotates each inspector element as UI Toolkit
  or IMGUI-drawn** — an immediate-mode editor has no retained controls to report, so its subtree
  dumps as a leaf, which is indistinguishable from a control that failed to draw. Pair with `unity_selection_set`'s
  `assetPaths`/`guids` to put an asset in the Inspector first; that tool reports anything that did
  not resolve and warns when an Inspector is locked and so will not follow the selection.
  `unity_uitk_repaint` and `unity_uitk_expand_inspector` close the loop, and both exist because a
  dump is only as good as what has actually been built:
  - a retained tree rebuilds only when its window *draws*, and an unfocused editor has no reason to
    redraw after a programmatic selection change;
  - the Inspector rebuilds its editor list from the active tracker, which `unity_selection_set` now
    forces — without it the window keeps showing the PREVIOUS object while the selection genuinely
    is the new one;
  - a collapsed component builds no inspector content at all, so dumping one reports nothing, which
    looks exactly like a component whose fields failed to draw.

  The full sequence is **select → expand (if inspecting components) → repaint → dump on a following
  call**. Each of those three failure modes yields an empty or stale dump that is indistinguishable
  from a genuine finding, which is why they are tools rather than documentation.

Tools that change editor state or write files carry `mutates: true` in `server/src/index.js` and are
left out of the read-only allowlist; `node server/src/index.js --list-readonly-tools` emits the safe set.

## Extending (adding routes/tools)

A new route is a `[McpRoute("...")]` static method in `plugin/Editor/Commands/` plus a matching
entry in `server/src/index.js` `TOOLS`. When developing one, test the route **via direct HTTP
against the bridge** before restarting your MCP client — the running MCP server process and its
registered tool list are stale until restart, but the bridge picks the route up as soon as Unity
recompiles the plugin:

```bash
curl -s -X POST http://127.0.0.1:7890/api/your/route -d '{"arg": 1}'
```

This keeps the edit → compile → probe loop inside one session; the MCP-level registration is the
only thing that needs the restart.

## Multi-instance

Open more than one editor and each binds its own port. `unity_list_instances` enumerates them;
`unity_select_instance` (or a per-call `port`/`project` override) targets one. A single editor
auto-selects.

Ports are not a stable identity: editors re-register across restarts and domain reloads and can
swap ports with each other. Prefer targeting by **project** — `unity_select_instance` with
`project: <substring of the project path or name>`, or the same `project` override on any call —
which resolves against the live instances and keeps following the project when its port moves.
A `port` override still works for one-off routing but can silently hit a different editor after
a reload; `project` and `port` are mutually exclusive on a call.

## Version fragility

The console reader uses internal `UnityEditor.LogEntries` reflection. On a Unity upgrade that renames
those members it fails with a **specific** error naming the unbound member, the Unity version, and the
file to fix (`plugin/Editor/Commands/ConsoleCommands.cs`).

My projects are currently using Unity 6.3 LTS, so that is what this tooling currently targets; if a
different version of Unity has differences for the methods/code accessed through reflection, certain
parts of this tooling may not work, but hopefully the errors make it clear why, rather than silently
failing in unexpected ways.

## Deliberately not included

Scene/asset mutation tools. I currently intend to keep this repo for providing Unity observability
features that Claude Code either doesn't have clean access to, or a more efficient way of accessing
info it already can through generic bash and grep commands. The compile trigger
(`unity_compile_request`) is the one deliberate exception — it closes the edit → compile → errors
feedback loop for an agent that edits scripts on disk, which is observability's missing half.

The **frame debugger** was out of scope until per-draw *batch-break reasons* were needed — the one
thing RenderDoc (https://renderdoc.org/, https://github.com/EdenLabs/agentic-renderdoc) cannot
label. It is in now (`unity_framedebugger_*`, reflection over the editor's internal
`FrameDebuggerUtility`); RenderDoc still covers GPU cost, pipeline state and pixel forensics better.

The **test runner**, and **package registry search**. These two would need results collected
across editor frames from async Unity APIs; the bridge's request-thread option (`RunOnRequestThread`,
used by `compile/status` to long-poll main-thread snapshots) provides the waiting half of that, but
the cross-frame result plumbing doesn't yet exist. Memory snapshots needed none: a capture's file is
its record, so the status route reads the file and answers across a domain reload.

## Licence

MIT — see `LICENSE`. Contains no third-party MCP/plugin code. The memory snapshot reader
(`plugin/Editor/Snapshot/`) was written from reading the Memory Profiler package's source
(`com.unity.memoryprofiler`, under the Unity Companion License) for the snapshot file format: its
layout and constants are adapted, and no code is copied.
