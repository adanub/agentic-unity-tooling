using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Adanub.UnityMcp.Editor
{
    /// <summary>
    /// Local HTTP server that runs inside the Unity Editor and exposes registered MCP
    /// routes to the external Node MCP shim. Listens on 127.0.0.1 only.
    ///
    /// Requests arrive on background threads (HttpListener / ThreadPool) but Unity APIs
    /// are main-thread-only, so every route handler is marshalled onto the main thread
    /// (pumped from <see cref="EditorApplication.update"/>) and the request thread blocks
    /// until it completes or times out.
    ///
    /// The server survives domain reloads (recompile, play-mode entry): it stops before a
    /// reload and a SessionState flag triggers a restart on the next load.
    ///
    /// A1 scope: fixed port, single instance, ping route only. Multi-instance discovery and
    /// dynamic port selection arrive in a later step.
    /// </summary>
    [InitializeOnLoad]
    public static class McpBridgeServer
    {
        // Each editor binds the first free port in this range, so multiple editors
        // (e.g. game client + game server) can run their bridges simultaneously.
        public const int PortRangeStart = 7890;
        public const int PortRangeEnd = 7899;

        private static HttpListener _listener;
        private static Thread _listenerThread;
        private static volatile bool _isRunning;
        private static int _activePort;

        private static readonly Queue<Action> _mainThreadQueue = new Queue<Action>();

        // How long a request thread waits for the main thread to service it.
        private const int MainThreadTimeoutMs = 30_000;

        // Persists "was running" across the domain reload so we restart automatically.
        private const string WasRunningKey = "Adanub_UnityMcp_WasRunning";

        public static bool IsRunning => _isRunning;
        public static int ActivePort => _isRunning ? _activePort : 0;

        static McpBridgeServer()
        {
            // Batch-mode subprocesses (asset import workers, CLI builds) must not claim the port.
            if (Application.isBatchMode) return;

            EditorApplication.update += OnEditorUpdate;
            EditorApplication.quitting += Stop;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;

            // Auto-start on load, and also restart if we were running before a reload.
            Start();
            SessionState.SetBool(WasRunningKey, false);
        }

        public static void Start()
        {
            if (_isRunning) return;
            if (Application.isBatchMode) return;

            for (int port = PortRangeStart; port <= PortRangeEnd; port++)
            {
                HttpListener listener = null;
                try
                {
                    listener = new HttpListener();
                    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    listener.Start();

                    _listener = listener;
                    _isRunning = true;
                    _activePort = port;

                    _listenerThread = new Thread(ListenLoop)
                    {
                        IsBackground = true,
                        Name = "Adanub Unity MCP Bridge",
                    };
                    _listenerThread.Start();

                    Debug.Log($"[Adanub MCP] Bridge started on http://127.0.0.1:{port}/  (project: {Application.productName})");
                    return;
                }
                catch (Exception ex)
                {
                    // Port busy (another editor's bridge, or an unrelated app) — try the next one.
                    try { listener?.Close(); } catch { }
                    if (port == PortRangeEnd)
                        Debug.LogError(
                            $"[Adanub MCP] Could not bind any port in {PortRangeStart}-{PortRangeEnd}: {ex.Message}. " +
                            "Close other Unity instances or free a port.");
                }
            }
        }

        public static void Stop()
        {
            if (!_isRunning && _listener == null) return;

            _isRunning = false;
            try
            {
                _listener?.Stop();
                _listener?.Close();
                _listenerThread?.Join(1000);
            }
            catch { /* shutting down */ }
            finally
            {
                _listener = null;
                _listenerThread = null;
                _activePort = 0;
            }

            Debug.Log("[Adanub MCP] Bridge stopped");
        }

        private static void OnBeforeAssemblyReload()
        {
            if (_isRunning)
            {
                SessionState.SetBool(WasRunningKey, true);
                Stop();
            }
        }

        // ─── Main-thread pump ───

        private static void OnEditorUpdate()
        {
            // Restart after a domain reload if we were running before it.
            if (!_isRunning && SessionState.GetBool(WasRunningKey, false))
            {
                Start();
                SessionState.SetBool(WasRunningKey, false);
            }

            ProcessMainThreadQueue();
        }

        private static void ProcessMainThreadQueue()
        {
            while (true)
            {
                Action action;
                lock (_mainThreadQueue)
                {
                    if (_mainThreadQueue.Count == 0) break;
                    action = _mainThreadQueue.Dequeue();
                }

                try { action?.Invoke(); }
                catch (Exception ex) { Debug.LogError($"[Adanub MCP] Main-thread action error: {ex}"); }
            }
        }

        private static object RunOnMainThread(Func<object> work)
        {
            if (Thread.CurrentThread.ManagedThreadId == 1)
                return work();

            object result = null;
            Exception error = null;
            using (var done = new ManualResetEventSlim(false))
            {
                lock (_mainThreadQueue)
                {
                    _mainThreadQueue.Enqueue(() =>
                    {
                        try { result = work(); }
                        catch (Exception ex) { error = ex; }
                        finally { done.Set(); }
                    });
                }

                if (!done.Wait(MainThreadTimeoutMs))
                    return new { error = $"Timed out after {MainThreadTimeoutMs / 1000}s waiting for the Unity main thread." };
            }

            if (error != null)
                return new { error = error.Message, stackTrace = error.StackTrace };

            return result;
        }

        // ─── HTTP listener ───

        private static void ListenLoop()
        {
            while (_isRunning)
            {
                try
                {
                    var context = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
                }
                catch (HttpListenerException) when (!_isRunning) { break; }
                catch (ObjectDisposedException) { break; }
                catch (ThreadAbortException) { break; }
                catch (Exception ex)
                {
                    if (_isRunning) Debug.LogError($"[Adanub MCP] Listener error: {ex.Message}");
                }
            }
        }

        private static void HandleRequest(HttpListenerContext context)
        {
            var response = context.Response;
            try
            {
                string path = context.Request.Url.AbsolutePath.TrimStart('/');
                if (!path.StartsWith("api/"))
                {
                    SendJson(response, 404, new { error = "Not found. Routes are served under /api/." });
                    return;
                }

                string route = path.Substring("api/".Length);

                string body = "";
                if (context.Request.HasEntityBody)
                {
                    using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                        body = reader.ReadToEnd();
                }

                object result = RunOnMainThread(() => Dispatch(route, body));
                SendJson(response, 200, result);
            }
            catch (Exception ex)
            {
                SendJson(response, 500, new { error = ex.Message, stackTrace = ex.StackTrace });
            }
        }

        private static object Dispatch(string route, string body)
        {
            if (!McpRouteRegistry.TryGet(route, out var entry))
                return new { error = $"Unknown route: {route}" };

            JObject args;
            try
            {
                args = string.IsNullOrEmpty(body) ? new JObject() : JObject.Parse(body);
            }
            catch (Exception ex)
            {
                return new { error = $"Invalid JSON body: {ex.Message}" };
            }

            return entry.Handler(args);
        }

        // Guard against multi-MB payloads (e.g. a full scene hierarchy) overwhelming the
        // stdio transport on the Node side. Handlers should paginate; this is the backstop.
        private const int ResponseHardLimitBytes = 12 * 1024 * 1024;

        private static void SendJson(HttpListenerResponse response, int statusCode, object data)
        {
            try
            {
                string json = JsonConvert.SerializeObject(data);
                byte[] buffer = Encoding.UTF8.GetBytes(json);

                if (buffer.Length > ResponseHardLimitBytes)
                {
                    json = JsonConvert.SerializeObject(new
                    {
                        error = "response_too_large",
                        sizeBytes = buffer.Length,
                        limitBytes = ResponseHardLimitBytes,
                        message = "Response exceeded the size limit. Use pagination args (maxNodes, maxDepth, limit, count, parentPath) to narrow it.",
                    });
                    buffer = Encoding.UTF8.GetBytes(json);
                    statusCode = 413;
                }

                response.StatusCode = statusCode;
                response.ContentType = "application/json";
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Adanub MCP] Failed to send response: {ex.Message}");
            }
            finally
            {
                try { response.OutputStream.Close(); } catch { }
            }
        }
    }
}
