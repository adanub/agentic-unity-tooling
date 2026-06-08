// HTTP client for the Unity Editor bridge (the C# HttpListener inside Unity).
//
// Each Unity editor binds the first free port in [PORT_RANGE_START, PORT_RANGE_END],
// so discovery is a scan of that range and every call targets a specific port.

const HOST = "127.0.0.1";
export const PORT_RANGE_START = 7890;
export const PORT_RANGE_END = 7899;

/**
 * Call a bridge route on a specific Unity instance. Returns the parsed JSON response.
 * @param {string} route  e.g. "ping" or "console/log"
 * @param {object} body   request payload (sent as JSON)
 * @param {number} port   target Unity instance port (required)
 */
export async function callBridge(route, body = {}, port) {
  if (!port) throw new Error("callBridge requires a target port.");
  const url = `http://${HOST}:${port}/api/${route}`;

  let res;
  try {
    res = await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body ?? {}),
      // Backstop for a frozen editor process (the bridge has its own 30s main-thread timeout, which
      // normally returns first; this only fires if the HTTP layer itself hangs).
      signal: AbortSignal.timeout(35000),
    });
  } catch (err) {
    throw new Error(
      `Could not reach the Unity bridge at ${url} (${err.message}). ` +
        `Is that Unity editor still open with the Adanub Unity MCP plugin loaded?`
    );
  }

  const text = await res.text();
  let json;
  try {
    json = JSON.parse(text);
  } catch {
    json = { raw: text };
  }

  if (!res.ok) {
    throw new Error(`Bridge route "${route}" returned HTTP ${res.status}: ${text}`);
  }

  return json;
}

/**
 * Ping a single port for discovery. Returns the ping payload, or null if nothing
 * responds within the timeout (port not in use, or not our bridge).
 */
export async function pingPort(port, timeoutMs = 300) {
  try {
    const res = await fetch(`http://${HOST}:${port}/api/ping`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: "{}",
      signal: AbortSignal.timeout(timeoutMs),
    });
    if (!res.ok) return null;
    const info = await res.json();
    return info && info.status === "ok" ? info : null;
  } catch {
    return null; // refused / timed out / not our bridge
  }
}
