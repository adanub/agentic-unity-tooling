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
    /// The server survives domain reloads (recompile, play-mode entry): it stops before a reload
    /// (beforeAssemblyReload) and the [InitializeOnLoad] static ctor restarts it on the next load.
    ///
    /// Binds the first free port in 7890-7899, so several editors (e.g. game client + server) can
    /// each run a bridge; the Node shim discovers them and routes per-call by port.
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

        // Captured on the main thread (the static ctor runs there) so RunOnMainThread can detect the
        // main thread without assuming it is managed-thread-id 1.
        private static int _mainThreadId;

        public static bool IsRunning => _isRunning;
        public static int ActivePort => _isRunning ? _activePort : 0;

        static McpBridgeServer()
        {
            // Batch-mode subprocesses (asset import workers, CLI builds) must not claim the port.
            if (Application.isBatchMode) return;

            _mainThreadId = Thread.CurrentThread.ManagedThreadId;

            EditorApplication.update += OnEditorUpdate;
            EditorApplication.quitting += Stop;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;

            // The static ctor runs on every domain load (including after a reload), so this
            // unconditionally (re)starts the bridge; beforeAssemblyReload stops it first.
            Start();
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
            if (_isRunning) Stop();
        }

        // ─── Main-thread pump ───

        private static void OnEditorUpdate() => ProcessMainThreadQueue();

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

        private sealed class CancelToken { public volatile bool Cancelled; }

        // Internal so RunOnRequestThread route handlers (which own their threading) can take
        // main-thread state snapshots between waits.
        internal static object RunOnMainThread(Func<object> work)
        {
            if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
                return work();

            object result = null;
            Exception error = null;
            // Deliberately NOT in a `using`: on timeout the request returns, but the queued action
            // may still run later. Disposing the event here would make that late Set() throw. The
            // cancel token makes the late action skip both the work and the Set; `done` is then GC'd.
            var done = new ManualResetEventSlim(false);
            var token = new CancelToken();

            lock (_mainThreadQueue)
            {
                _mainThreadQueue.Enqueue(() =>
                {
                    if (token.Cancelled) return;
                    try { result = work(); }
                    catch (Exception ex) { error = ex; }
                    finally { if (!token.Cancelled) done.Set(); }
                });
            }

            if (!done.Wait(MainThreadTimeoutMs))
            {
                token.Cancelled = true;
                return new { error = $"Timed out after {MainThreadTimeoutMs / 1000}s waiting for the Unity main thread." };
            }

            done.Dispose();
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

                object result;
                if (McpRouteRegistry.TryGet(route, out var entry) && entry.RunOnRequestThread)
                {
                    // Long-polling handler: runs on this request thread and hops to the main
                    // thread itself per snapshot, so the editor never blocks on the wait.
                    try { result = Invoke(entry, body); }
                    catch (Exception ex) { result = new { error = ex.GetBaseException().Message, stackTrace = ex.GetBaseException().StackTrace }; }
                }
                else
                {
                    result = RunOnMainThread(() => Dispatch(route, body));
                }
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

            return Invoke(entry, body);
        }

        private static object Invoke(McpRouteRegistry.RouteEntry entry, string body)
        {
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
                // An in-flight response losing its listener to Stop() is expected during a domain
                // reload (e.g. a compile/status long-poll spanning a clean compile) — the client
                // retries after the reload, so don't pollute the console for it.
                bool benignShutdown = !_isRunning && (ex is ObjectDisposedException || ex is HttpListenerException);
                if (!benignShutdown)
                    Debug.LogError($"[Adanub MCP] Failed to send response: {ex.Message}");
            }
            finally
            {
                try { response.OutputStream.Close(); } catch { }
            }
        }
    }
}
