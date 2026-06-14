# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`agentic-unity-tooling` is a standalone, **publishable, project-agnostic** MCP toolset (package id
`com.adanub.unity-mcp`, MIT) for **observing and inspecting a running Unity Editor** from an AI agent:
console logs, compilation errors, profiler/memory data, and scene/asset/prefab/project state. It is
read-focused by design — the *only* write paths are a script-compile trigger and three small editor-state
tools (console clear, selection set, scene-view focus).

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
invocation). `CompileStatusRoute`/`ConsoleCommands` is the reference.

### Domain-reload survival

A recompile or play-mode entry triggers a Unity **domain reload** that tears down and rebuilds the plugin's
managed state mid-call. Both halves are built to ride this out, and changes must preserve it:

- **plugin**: `McpBridgeServer` is `[InitializeOnLoad]`; it `Stop()`s on `beforeAssemblyReload` and the static
  ctor restarts it on the next load. The compile *session* and captured compiler messages persist across the
  reload via `SessionState` (`ConsoleCommands.cs`).
- **server**: `callWithRecovery` (`instances.js`) retries connection-level failures (`ECONNREFUSED` etc.) with
  exponential backoff, **re-locating the editor by `projectPath`** if the reload moved it to a different port.
  All routes must stay idempotent / harmlessly re-runnable because retried calls re-execute on the bridge.

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

There is no test suite, linter, or plugin build step. The plugin compiles when a Unity editor with the
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

## Mutating tools and the allowlist

Four tools change editor state and carry `mutates: true` in `server/src/index.js`: `unity_console_clear`,
`unity_selection_set`, `unity_selection_focus_scene_view`, `unity_compile_request` (plus the orchestrated
`unity_compile`). `--list-readonly-tools` emits everything *except* these as
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
  `unity_compile`).
- The `plugin/` `.meta` files are **tracked** (it's a UPM package); don't gitignore them.

## Deliberately out of scope

Scene/asset *mutation* tools (this is observability, not "AI builds your scene"); a frame debugger (external
GPU capture tooling covers it better); the test runner and package-registry search (both need results
collected across editor frames from async Unity APIs — the request-thread waiting half exists, but the
cross-frame result plumbing does not). See `README.md` for the rationale before adding any of these.
