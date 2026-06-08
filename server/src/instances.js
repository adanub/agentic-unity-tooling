// Multi-instance discovery + selection.
//
// Several Unity editors (e.g. game client + game server) can run bridges at once,
// each on its own port. This module finds them, tracks which one tools target, and
// resolves the port for each call:
//   1. an explicit per-call `port` always wins (parallel-safe routing);
//   2. otherwise the persisted/auto selection is used (validated still-alive);
//   3. a single discovered editor auto-selects; multiple require unity_select_instance.

import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import crypto from "node:crypto";

import { pingPort, PORT_RANGE_START, PORT_RANGE_END } from "./bridge.js";

// Per-project selection file: the MCP server is launched once per project (Claude Code sets
// CLAUDE_PROJECT_DIR in the server's env), so key the state by it — two projects' shims then never
// clobber each other's instance selection.
const PROJECT_KEY = crypto
  .createHash("sha1")
  .update(process.env.CLAUDE_PROJECT_DIR || process.cwd())
  .digest("hex")
  .slice(0, 12);
const STATE_FILE = path.join(os.tmpdir(), `adanub-unity-mcp-selection-${PROJECT_KEY}.json`);

let _selection = undefined; // { port, projectPath } | null | undefined(=not loaded)

function loadSelection() {
  if (_selection !== undefined) return;
  try {
    _selection = JSON.parse(fs.readFileSync(STATE_FILE, "utf8"));
  } catch {
    _selection = null;
  }
}

function saveSelection(sel) {
  _selection = sel;
  try {
    if (sel) fs.writeFileSync(STATE_FILE, JSON.stringify(sel));
    else fs.rmSync(STATE_FILE, { force: true });
  } catch {
    /* best-effort persistence */
  }
}

/** Scan the port range and return all responding Unity bridges. */
export async function discoverInstances() {
  const ports = [];
  for (let p = PORT_RANGE_START; p <= PORT_RANGE_END; p++) ports.push(p);

  const results = await Promise.all(
    ports.map(async (port) => {
      const info = await pingPort(port);
      if (!info) return null;
      return {
        port: info.port ?? port,
        projectName: info.projectName ?? "(unknown)",
        projectPath: info.projectPath ?? "",
        unityVersion: info.unityVersion ?? "",
        processId: info.processId ?? null,
        isPlaying: !!info.isPlaying,
      };
    })
  );

  return results.filter(Boolean).sort((a, b) => a.port - b.port);
}

/** Explicitly select an instance by port. Validates it is reachable first. */
export async function selectInstance(port) {
  const info = await pingPort(port, 800);
  if (!info) {
    return { error: `No Adanub MCP bridge responded on port ${port}. Use unity_list_instances to see what's running.` };
  }
  saveSelection({ port, projectPath: info.projectPath ?? "" });
  return {
    selected: {
      port,
      projectName: info.projectName,
      projectPath: info.projectPath,
      unityVersion: info.unityVersion,
    },
  };
}

/**
 * Resolve the target port for a tool call.
 * Returns one of: { port }, { needsSelection: [...] }, or { error }.
 */
export async function resolveTargetPort(explicitPort) {
  if (explicitPort) return { port: explicitPort };

  loadSelection();
  if (_selection && _selection.port) {
    // Validate the persisted selection still points at the same project (ports get reused).
    const info = await pingPort(_selection.port, 800);
    if (info && (!_selection.projectPath || info.projectPath === _selection.projectPath)) {
      return { port: _selection.port };
    }
    saveSelection(null); // stale — fall through to rediscovery
  }

  const instances = await discoverInstances();
  if (instances.length === 0) {
    return {
      error:
        `No Unity editor with the Adanub MCP bridge was found (scanned ports ` +
        `${PORT_RANGE_START}-${PORT_RANGE_END}). Open Unity with the plugin loaded.`,
    };
  }
  if (instances.length === 1) {
    saveSelection({ port: instances[0].port, projectPath: instances[0].projectPath });
    return { port: instances[0].port, autoSelected: instances[0] };
  }
  return { needsSelection: instances };
}

export function describeInstances(instances) {
  return instances
    .map(
      (i) =>
        `  • port ${i.port}: ${i.projectName}` +
        (i.isPlaying ? " [playing]" : "") +
        (i.projectPath ? `\n      ${i.projectPath}` : "")
    )
    .join("\n");
}
