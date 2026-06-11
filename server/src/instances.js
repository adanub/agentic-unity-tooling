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

import { callBridge, pingPort, PORT_RANGE_START, PORT_RANGE_END } from "./bridge.js";

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
 * Returns one of: { port, projectPath? }, { needsSelection: [...] }, or { error }.
 */
export async function resolveTargetPort(explicitPort) {
  if (explicitPort) return { port: explicitPort };

  loadSelection();
  let stalePath = "";
  if (_selection && _selection.port) {
    // Validate the persisted selection still points at the same project (ports get reused).
    const info = await pingPort(_selection.port, 800);
    if (info && (!_selection.projectPath || info.projectPath === _selection.projectPath)) {
      return { port: _selection.port, projectPath: _selection.projectPath || info.projectPath || "" };
    }
    stalePath = _selection.projectPath || "";
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

  // A domain reload rebinds the first free port, so the selected editor can move (and with two
  // editors open, swap ports with the other). Follow the project rather than re-prompting.
  if (stalePath) {
    const moved = instances.find((i) => i.projectPath === stalePath);
    if (moved) {
      saveSelection({ port: moved.port, projectPath: moved.projectPath });
      return { port: moved.port, projectPath: moved.projectPath };
    }
  }

  if (instances.length === 1) {
    saveSelection({ port: instances[0].port, projectPath: instances[0].projectPath });
    return { port: instances[0].port, projectPath: instances[0].projectPath, autoSelected: instances[0] };
  }
  return { needsSelection: instances };
}

// ─── Transient-failure recovery (Unity domain reloads) ───
//
// A recompile's domain reload stops the bridge for the duration of the reload and can move it
// to a different port afterwards. Calls that land in that window fail with connection errors;
// retry with backoff, re-locating the project's bridge by projectPath between attempts.

const MAX_RETRIES = 8;
const RETRY_BASE_DELAY_MS = 750;
const RETRY_MAX_DELAY_MS = 8000; // worst case ≈ 43s of backoff — covers a heavy project's reload

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

function isTransientError(err) {
  const code = err?.cause?.code || err?.code || "";
  const msg = err?.message || "";
  return (
    code === "ECONNREFUSED" ||
    code === "ECONNRESET" ||
    code === "UND_ERR_SOCKET" ||
    msg.includes("ECONNREFUSED") ||
    msg.includes("ECONNRESET") ||
    msg.includes("fetch failed") ||
    msg.includes("socket hang up")
  );
}

/**
 * callBridge with retry/backoff on connection-level failures. `target` is the object returned
 * by resolveTargetPort ({ port, projectPath? }); when projectPath is known, the retry loop
 * re-discovers the project's bridge between attempts in case the reload moved it to a new port.
 * Note retried calls re-execute on the bridge — all routes are idempotent or harmlessly
 * re-runnable (compile/request just re-triggers a refresh).
 */
export async function callWithRecovery(route, args, target) {
  let port = target.port;
  for (let attempt = 0; ; attempt++) {
    try {
      return await callBridge(route, args, port);
    } catch (err) {
      if (!isTransientError(err) || attempt >= MAX_RETRIES) throw err;

      const delay = Math.min(RETRY_BASE_DELAY_MS * 2 ** attempt, RETRY_MAX_DELAY_MS);
      console.error(
        `[adanub-unity-mcp] ${route} on port ${port} unreachable (domain reload?) — ` +
          `retry ${attempt + 1}/${MAX_RETRIES} in ${delay}ms`
      );
      await sleep(delay);

      if (target.projectPath) {
        const instances = await discoverInstances();
        const found = instances.find((i) => i.projectPath === target.projectPath);
        if (found && found.port !== port) {
          console.error(`[adanub-unity-mcp] ${target.projectPath} moved from port ${port} to ${found.port}`);
          port = found.port;
          saveSelection({ port, projectPath: target.projectPath });
        }
      }
    }
  }
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
