# agentic-unity-tooling

An MCP toolset for **observing and inspecting** a running Unity Editor from an AI agent —
console logs, compilation errors, profiler/memory data, and scene/asset/prefab/project state.
Read-focused by design (not an "AI builds your scene" tool). Project-agnostic and reusable.

Designed to be automatically setup by Claude Code with minimal user intervention needed.

Package id `com.adanub.unity-mcp`. MIT-licensed. Clean-room implementation.

## Architecture

```
Claude ──stdio──▶ server/ (Node MCP shim) ──HTTP 127.0.0.1:789x──▶ plugin/ (C# bridge inside Unity)
```

Unity Editor code can't itself be an MCP stdio process, so the tool is split:

| Part      | What it is                                                                            |
|-----------|---------------------------------------------------------------------------------------|
| `plugin/` | Unity UPM package (`com.adanub.unity-mcp`). An `HttpListener` bridge + reflection-dispatched route handlers, Editor-only. Routes self-register via `[McpRoute("...")]` — adding a tool is a self-contained command class, no central switch. |
| `server/` | Node MCP stdio server. Exposes `unity_*` tools that forward to the bridge.            |

The bridge marshals every call onto Unity's main thread, survives domain reloads, and binds the
first free port in **7890–7899** so multiple editors (e.g. game client + server) run side by side.

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

## Tools (read-only)

- **Observe**: console log (collapses per-frame spam with counts; reads Unity's own Console store),
  compilation errors (survive domain reload), editor/project/scene state, profiler stats/memory/
  frame-data/analyze, asset memory breakdown + top consumers.
- **Inspect**: scene hierarchy (bounded), search by name/component/tag/layer/shader, asset search,
  missing references, selection, GameObject + component properties, prefab info/hierarchy/
  variant overrides.
- **Assets/graphics**: asset list, script read, ScriptableObject props + type list, shader list +
  properties, mesh/material/texture/renderer info, lighting summary, texture import settings.
- **Project config**: tags/layers, physics collision matrix, assembly definitions, sprite atlases,
  input actions, packages, scene-view camera, editor/player prefs.

Three tools change editor state and are excluded from the default read-only allowlist (`console_clear`,
`selection_set`, `selection_focus_scene_view`). `node server/src/index.js --list-readonly-tools`
emits the safe set.

## Multi-instance

Open more than one editor and each binds its own port. `unity_list_instances` enumerates them;
`unity_select_instance` (or a per-call `port`) targets one. A single editor auto-selects.

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
info it already can through generic bash and grep commands.

The **frame debugger** - renderdoc already covers most relevant use cases better than the
frame debugger does, see https://renderdoc.org/ and https://github.com/EdenLabs/agentic-renderdoc
; its one unique use-case is per-draw *batch-break reasons*, which would be an easy reflection-only
add if ever needed.

The **test runner**, and **package registry search**. These two would need
an async/deferred bridge path (a route that resolves across editor frames instead of returning
synchronously, so the mainthread can keep ticking to advance the async Unity API) that the current
synchronous bridge omits.

## Licence

MIT — see `LICENSE`. Contains no third-party MCP/plugin code.
