# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`agentic-unity-tooling` is a standalone, **publishable, project-agnostic** MCP toolset (package id
`com.adanub.unity-mcp`, MIT) for **observing and inspecting a running Unity Editor** from an AI agent:
console logs, compilation errors, profiler/memory data, frame-debugger events, and
scene/asset/prefab/project state. It is read-focused by design — the write paths are a script-compile
trigger and a small set of editor-state controls (console clear, selection set, scene-view focus,
profiler record on/off, frame debugger on/off, window show/close, play mode, scene open, menu item) and a memory snapshot capture (a file written into the
project's `MemoryCaptures` folder),
each marked mutating; nothing edits a scene or an asset.

**Keep this repo 100% generic.** It is vendored into private projects but is meant to be reused and
published on its own. Do **not** introduce names of any specific consuming project, game, or company into
code, comments, docs, or examples. Generic terms like "game client + game server" (for the multi-instance
case) are fine; concrete project names are not. Anything project-specific belongs in the consuming
project's skill wrapper, never here.

## Architecture

```
MCP client ──stdio──▶ server/ (Node MCP shim) ──HTTP 127.0.0.1:789x──▶ plugin/ (C# bridge inside Unity)
```

A Unity Editor cannot itself be an MCP stdio process, so the tool is split in two halves that must be kept
in sync:

- **`plugin/`** — Unity UPM package (`com.adanub.unity-mcp`), Editor-only (`Adanub.UnityMcp.Editor`
  asmdef). An `HttpListener` bridge (`McpBridgeServer.cs`) plus reflection-dispatched route handlers under
  `Editor/Commands/`. Depends only on `com.unity.nuget.newtonsoft-json`.
- **`server/`** — Node MCP stdio server (`src/index.js` + `bridge.js` + `instances.js`). ESM, Node ≥18,
  single dependency `@modelcontextprotocol/sdk`. Exposes `unity_*` tools that forward to the bridge.

### Route registration (the core extension pattern)

Routes self-register via reflection — there is **no central switch**. `McpRouteRegistry` scans the plugin
assembly for any `static object Method(JObject args)` decorated with `[McpRoute("route/path")]` and builds
an immutable route map (lazily, double-checked-locked, rebuilt fresh on each domain reload). Adding a tool
is therefore two self-contained edits:

1. **plugin**: a new `[McpRoute("foo/bar")]` static method in an `Editor/Commands/*.cs` command class
   (signature must be exactly `static object Foo(JObject args)` or it's rejected at scan time with a logged
   error).
2. **server**: a matching entry appended to the `TOOLS` array in `server/src/index.js` (`{ name, description,
   inputSchema, route, mutates? }`). `TOOLS` has no dispatch logic — it's pure data that the generic
   `CallToolRequestSchema` handler forwards by `route`.

The two sides are independent and must agree on the route string. See "Dev loop" below.

### Threading model (critical)

HTTP requests arrive on background `HttpListener`/`ThreadPool` threads, but **Unity APIs are main-thread-only**.
`McpBridgeServer.RunOnMainThread` marshals each handler onto the main thread via a queue pumped from
`EditorApplication.update`, blocking the request thread until it completes (30s timeout). Handlers therefore
run on the main thread by default and may freely touch Unity APIs.

The exception is `[McpRoute(..., RunOnRequestThread = true)]`: the handler runs on the request thread (for
long-polling, e.g. `compile/status`) so the editor doesn't block on the wait. **Such handlers must NOT touch
Unity APIs directly** — they call `RunOnMainThread` for each state snapshot, and must be declared on a type
with **no static initialiser that touches Unity** (the type's static ctor runs on the request thread on first
invocation). `CompileStatusRoute`/`ConsoleCommands` is the reference. A route that must instead WAIT on an
editor event (`PlayModeWaitRoute`) reads state kept in a separate holder type that the event writes under a
lock and pulses; `McpBridgeServer`'s static ctor runs that holder's static ctor explicitly, on the main thread,
before `Start()`, because `[InitializeOnLoad]` order within one assembly is unspecified.

### Domain-reload survival

A recompile or play-mode entry triggers a Unity **domain reload** that tears down and rebuilds the plugin's
managed state mid-call. Both halves are built to ride this out, and changes must preserve it:

- **plugin**: `McpBridgeServer` is `[InitializeOnLoad]`; it `Stop()`s on `beforeAssemblyReload` and the static
  ctor restarts it on the next load. The compile *session* and captured compiler messages persist across the
  reload via `SessionState` (`ConsoleCommands.cs`).
- **server**: `callUnity` (`instances.js`) unifies target resolution and the call in **one retry loop** —
  every attempt re-resolves (explicit port → pinned project identity → persisted selection → discovery) and
  then calls, with exponential backoff (~43s budget). This matters because the bridge port is dark for the
  *entire* reload: a resolution step outside the loop fails instantly on calls that land in that window
  (the original two-layer design's exact bug). Resolution never deletes the persisted selection on a missed
  ping — only budget exhaustion clears it. Multi-call chains (compile request→status) pass `pinnedPath` so
  the chain can't split across editors mid-reload. All routes must stay idempotent / harmlessly re-runnable
  because retried calls re-execute on the bridge.

The `compile/request` trigger uses `AssetDatabase.Refresh()` deferred onto `EditorApplication.update`
(deliberately **not** `delayCall`, which an unfocused editor can defer indefinitely) so it works with the
editor in the background.

### Multi-instance

Each editor binds the **first free port in 7890–7899**, so several editors run bridges side by side. The Node
shim discovers them (`discoverInstances` pings the range), persists a per-project selection
(`unity_select_instance`), and every forwarding tool accepts an optional per-call `port` that overrides the
selection for parallel-safe routing. A single discovered editor auto-selects. Selection state is keyed by
`CLAUDE_PROJECT_DIR` so two consuming projects' shims never clobber each other.

## Commands

```bash
# server deps (only build/install step — the plugin is compiled by Unity itself)
npm --prefix server install

# run the MCP server standalone (normally launched by the MCP client via stdio)
node server/src/index.js

# emit the read-only tool permission names for an allowlist (excludes mutates:true tools)
node server/src/index.js --list-readonly-tools
```

There is no linter or plugin build step, and the one test project is `tests/SnapshotReader.Tests` (headless, for the
engine-free snapshot code — see "Memory snapshots"). The plugin compiles when a Unity editor with the
package loaded recompiles; verify plugin changes by watching for the bridge's startup log
(`[Adanub MCP] Bridge started on http://127.0.0.1:789x/`) and exercising routes.

### Dev loop for a new/changed route

The running MCP **server** process and its registered tool list are stale until the MCP client restarts, but
the **bridge** picks up a route as soon as Unity recompiles the plugin. So test the route via **direct HTTP**
before restarting the client — this keeps edit → compile → probe inside one session:

```bash
curl -s -X POST http://127.0.0.1:7890/api/your/route -d '{"arg": 1}'
```

Only the MCP-level tool registration (the `TOOLS` entry) needs the client restart.

## Memory snapshots

`Commands/MemorySnapshotCommands.cs` holds three routes: `memory/snapshot` (main thread; starts a
capture with `MemoryProfiler.TakeSnapshot` and returns its path), `memory/snapshot-status` and
`memory/snapshot-diff` (request thread; they read files and touch no Unity API). The reading lives in
`plugin/Editor/Snapshot/` and is **engine-free** — no Unity types — because
`tests/SnapshotReader.Tests` compile-links it and runs headless, so a Unity type creeping in breaks
that build. `dotnet run --project tests/SnapshotReader.Tests` runs the synthetic snapshots and broken
files; add `-- <capture.snap> [editor-counts.json]` for a real capture, or `-- diff <before> <after>`.
The synthetic writer shares the reader's model of the format, so only a real capture proves the model —
the first real read caught its one mistake (an equal-size array's count is the LOW 32 bits of its
chapter header's ulong).

What the routes are built around, each measured:

- **A capture's file is its only record.** The start route returns the path at once; the status route
  reports finished when the file validates, so a capture survives a domain reload with no job state.
- **`TakeSnapshot` captures inside the call and runs its finish callback before returning.** A capture
  started inside that callback is accepted silently, never completes, and cancels every later capture
  ("Canceling snapshot, there is another snapshot in progress.") until a domain reload. Never start one
  from a callback; the routes never do.
- **`TakeSnapshot` deletes an empty directory at its path** and writes the capture in its place, so the
  start route refuses a path a directory holds.
- **D3D12 frees a disposed GPU buffer late**: a capture in the tick of the release still holds it, under
  the root `Rendering: D3D12GfxDevice`. A leak check captures a few frames later.
- **Owners match by name, not ID**: resource and object IDs change when something is recreated (a domain
  reload recreates render textures and large buffers), so the new-resource lists are noisy across a
  reload while the by-root totals are not. Root IDs of 0 or less are unrooted and zero-size GPU resources
  are skipped, as the Memory Profiler treats them. A never-bound GraphicsBuffer is a GPU resource under
  `Rendering: GraphicsBuffers`; a Persistent NativeArray is an allocation under
  `UnsafeUtility: Malloc(Persistent)`. An editor capture records no per-allocator allocation counts.
- **The default flags are NativeObjects + NativeAllocations**: every chapter the diff reads, about 45 MB
  for an editor, where the Memory Profiler window's default set (adding the managed heap and allocation
  stack traces) runs to about 1.5 GB.
- Unity 6.3 writes snapshot format version 17 (4-byte instance IDs; 8-byte from version 18).

## Mutating tools and the allowlist

Tools that change editor state carry `mutates: true` in `server/src/index.js`: `unity_console_clear`,
`unity_selection_set`, `unity_selection_focus_scene_view`, `unity_uitk_expand_inspector`,
`unity_uitk_set_foldout`, `unity_profiler_record`, `unity_compile_request`
(plus the orchestrated `unity_compile`), the Frame Debugger's `enable` / `event_data` / `disable`,
`unity_window_show` / `unity_window_close`, `unity_editor_playmode`, `unity_scene_open` and
`unity_editor_menu_item`, and the memory snapshot capture (`unity_memory_snapshot`,
`unity_memory_snapshot_start`). `--list-readonly-tools` emits everything *except* these as
`mcp__adanub-unity-mcp__<name>` permission strings — consuming projects use that to auto-generate their
read-only allowlist instead of hand-maintaining it. When adding a state-changing tool, set `mutates: true`
so it's excluded from the safe set.

## Version fragility

Console reading uses internal `UnityEditor.LogEntries` reflection (`ConsoleCommands.cs`) — there is no public
API for Unity's own console store. The reflection is resolved once and guarded: on a Unity upgrade that
renames those members it fails with a **specific** error naming the unbound member and the file to fix, rather
than silently. Target is Unity 6.x (`unity: "6000.0"` in `plugin/package.json`); other versions may differ in
the reflected members.

## Response size

The bridge hard-caps responses at 12 MB (`McpBridgeServer.ResponseHardLimitBytes`) and returns a
`response_too_large` error pointing at the pagination args. Handlers that can produce large output (scene
hierarchy, asset/search lists) **must** support and honour bounding args (`maxNodes`, `maxDepth`, `limit`,
`count`, `parentPath`); the cap is only the backstop.

## Code conventions

- **C# (plugin)**: namespace `Adanub.UnityMcp.Editor[.Commands]`; one command class per concern under
  `Editor/Commands/`; PascalCase members. Each command class is self-contained — never add a central
  dispatch/registry edit for a new route. Reflection-resolved internal-API access must degrade gracefully
  with a descriptive error, never an unguarded throw.
- **JS (server)**: ESM, `unity_*` tool names, `route` strings matching the plugin's `[McpRoute]`. Keep
  `index.js` declarative — the `CallToolRequestSchema` handler is generic; don't add per-tool branches there
  (the only locally-handled, non-forwarding tools are instance management and the orchestrated
  `unity_compile` and `unity_memory_snapshot`).
- The `plugin/` `.meta` files are **tracked** (it's a UPM package); don't gitignore them.

## Deliberately out of scope

Scene/asset *mutation* tools (this is observability, not "AI builds your scene"); driving a window's
controls (clicking buttons, setting fields — a deliberate decision for whoever needs it, not an
inheritance); RenderDoc capture triggering (the trigger is one call, and everything that makes a capture
worth analysing — scene, camera, controls, naming — is the user's); the test runner and package-registry
search (both need results collected across editor frames from async Unity APIs — the request-thread
waiting half exists, but the cross-frame result plumbing does not; memory snapshots needed none, because a capture's
file is its record). See `README.md` for the rationale
before adding any of these. The frame debugger WAS on this list until per-draw batch-break reasons were
needed; `FrameDebuggerCommands.cs` is the reflection-only add the README anticipated, and its two
preconditions (the editor application must have OS focus for the Game view to render; per-event detail
is one re-render per event) are reported, never worked around.
