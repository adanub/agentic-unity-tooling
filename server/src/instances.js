// Multi-instance discovery + selection + retried bridge calls.
//
// Several Unity editors (e.g. two related projects) can run bridges at once, each on
// its own port. This module finds them, tracks which one tools target, and executes
// every bridge call through a single resolve-then-call retry loop: a call that lands
// during a domain reload (the bridge port is dark for the whole reload, and the
// editor can come back on a different port) re-resolves and retries with backoff
// instead of failing fast. Resolution order, re-run on every attempt:
//   1. an explicit per-call `port` always wins (parallel-safe routing);
//   2. a pinned project identity (multi-call chains like compile request→status);
//   3. the persisted selection, following the project if its port moved;
//   4. discovery: a single live editor auto-selects; multiple require selection.

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
 * Resolve a project query (case-insensitive substring of the project path or name) to a live
 * instance. Ports shuffle between editors across restarts and domain reloads, so a project
 * identity is the stable way to target one. A couple of short retries ride out an editor whose
 * bridge is dark mid-reload at the moment of the query.
 */
export async function resolveProjectIdentity(query) {
  const q = String(query).toLowerCase();
  let matches = [];
  for (let attempt = 0; attempt < 3; attempt++) {
    const instances = await discoverInstances();
    matches = instances.filter(
      (i) =>
        (i.projectPath || "").toLowerCase().includes(q) ||
        (i.projectName || "").toLowerCase().includes(q)
    );
    if (matches.length === 1) return { instance: matches[0] };
    if (matches.length > 1) {
      return {
        error:
          `Project query '${query}' matches ${matches.length} editors — be more specific:\n` +
          describeInstances(matches),
      };
    }
    await sleep(750 * (attempt + 1));
  }
  return {
    error:
      `No running editor matches project '${query}' (bridges may be mid-reload — ` +
      `retried 3 scans). Use unity_list_instances to see what's running.`,
  };
}

/** Thrown when several live editors are found and nothing disambiguates between them. */
export class InstanceSelectionRequired extends Error {
  constructor(instances) {
    super("Multiple Unity editors are open — select one first.");
    this.name = "InstanceSelectionRequired";
    this.instances = instances;
  }
}

// ─── Retry budget (Unity domain reloads) ───
//
// A recompile's domain reload stops the bridge for the duration of the reload and can move it
// to a different port afterwards. Both the resolution scan and the call itself ride that window
// inside ONE loop, with enough exponential backoff to cover a heavy project's reload.

const MAX_RETRIES = 8;
const RETRY_BASE_DELAY_MS = 750;
const RETRY_MAX_DELAY_MS = 8000;

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const backoff = (attempt) => Math.min(RETRY_BASE_DELAY_MS * 2 ** attempt, RETRY_MAX_DELAY_MS);
const RETRY_BUDGET_SECONDS = Math.round(
  Array.from({ length: MAX_RETRIES }, (_, i) => backoff(i)).reduce((a, b) => a + b, 0) / 1000
);

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
 * Execute a bridge route with resolution and retry unified in one loop.
 *
 * opts.explicitPort — per-call port override; called even while dark (the failure is
 *   transient and retried), and once its project identity has been seen alive the loop
 *   follows that project if a reload moves it. Never touches the saved selection.
 * opts.pinnedPath — pin the target to a project identity across a multi-call chain
 *   (compile request→status), so related calls can never split across editors mid-reload.
 *
 * Returns { result, port, projectPath }. Throws InstanceSelectionRequired when several
 * live editors need disambiguating, immediately (live editors — nothing to wait for).
 * Note retried calls re-execute on the bridge — all routes are idempotent or harmlessly
 * re-runnable (compile/request just re-triggers a refresh).
 */
export async function callUnity(route, args, { explicitPort, pinnedPath } = {}) {
  loadSelection();
  const selectionBacked = !explicitPort;
  const hadSelection = !!(_selection && _selection.port);
  let identity = pinnedPath || (selectionBacked && hadSelection ? _selection.projectPath || "" : "");

  // One resolution pass. Returns a port to call, or null when nothing is callable this
  // round (mid-reload, or our project's old port is now owned by a different editor).
  const resolveOnce = async (attempt) => {
    const startPort = explicitPort || (_selection ? _selection.port : 0);
    if (startPort) {
      const info = await pingPort(startPort, 800);
      // With no identity yet, whatever answers on an explicit/selected port IS the target.
      if (info && (!identity || info.projectPath === identity)) {
        identity ||= info.projectPath || "";
        return startPort;
      }
      // Dark, or the port was reused by a different project — follow our project instead.
    }

    if (identity) {
      const instances = await discoverInstances();
      const ours = instances.find((i) => i.projectPath === identity);
      if (ours) {
        if (selectionBacked && ours.port !== _selection?.port) {
          console.error(`[adanub-unity-mcp] ${identity} moved to port ${ours.port}`);
          saveSelection({ port: ours.port, projectPath: identity });
        }
        return ours.port;
      }
      // Our editor is down. If another project's editor now owns the port we'd call,
      // calling it would silently succeed against the wrong project — wait instead.
      if (explicitPort && !instances.some((i) => i.port === explicitPort)) return explicitPort;
      return null;
    }

    // No identity at all: an explicit port that has never answered is still the target.
    if (explicitPort) return explicitPort;

    const instances = await discoverInstances();
    if (instances.length === 1) {
      identity = instances[0].projectPath || "";
      saveSelection({ port: instances[0].port, projectPath: identity });
      return instances[0].port;
    }
    if (instances.length > 1) throw new InstanceSelectionRequired(instances);

    // Nothing live. Fail fast only when there was never evidence of an editor to wait for.
    if (attempt === 0 && !hadSelection && !pinnedPath) {
      throw new Error(
        `No Unity editor with the Adanub MCP bridge was found (scanned ports ` +
          `${PORT_RANGE_START}-${PORT_RANGE_END}). Open Unity with the plugin loaded.`
      );
    }
    return null;
  };

  for (let attempt = 0; ; attempt++) {
    const port = await resolveOnce(attempt);
    if (port) {
      try {
        const result = await callBridge(route, args, port);
        return { result, port, projectPath: identity };
      } catch (err) {
        if (!isTransientError(err)) throw err;
      }
    }

    if (attempt >= MAX_RETRIES) throw await exhaustedError(route, { explicitPort, identity, hadSelection });

    console.error(
      `[adanub-unity-mcp] ${route}: bridge unreachable (domain reload?) — ` +
        `retry ${attempt + 1}/${MAX_RETRIES} in ${backoff(attempt)}ms`
    );
    await sleep(backoff(attempt));
  }
}

// The target never came back inside the budget. Clear a selection-backed target's saved
// selection so a genuinely-closed editor doesn't tax every later call with the full wait —
// the next call auto-selects a single live editor or asks.
async function exhaustedError(route, { explicitPort, identity, hadSelection }) {
  const basis = explicitPort ? `port ${explicitPort}` : identity ? `the editor for ${identity}` : "any Unity editor";
  let cleared = "";
  if (!explicitPort && hadSelection) {
    saveSelection(null);
    cleared = " The saved instance selection was cleared; the next call re-discovers.";
  }
  let alive = "";
  try {
    const instances = await discoverInstances();
    if (instances.length > 0)
      alive = `\nEditors currently running:\n${describeInstances(instances)}`;
  } catch {
    /* diagnostic only */
  }
  return new Error(
    `"${route}" gave up: ${basis} did not respond within the retry budget (~${RETRY_BUDGET_SECONDS}s). ` +
      `Is that editor still open with the plugin loaded?${cleared}${alive}`
  );
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
