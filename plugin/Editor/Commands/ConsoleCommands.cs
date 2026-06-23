using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>
    /// Console + compilation diagnostics.
    ///
    /// Console logs are read straight from Unity's own Console backing store
    /// (UnityEditor.LogEntries) via reflection — NOT a parallel buffer. This returns exactly
    /// what the Console shows (respecting its type toggles, search filter, and Collapse),
    /// exposes per-message repeat counts, and never evicts rare logs the way a fixed ring
    /// buffer would under per-frame spam. Trade-off: internal API, version-fragile — guarded
    /// with reflection and a graceful error if it can't bind.
    ///
    /// Compiler errors/warnings are captured separately via CompilationPipeline (structured
    /// file/line/column) and persisted across the domain reload so warnings survive recompiles.
    ///
    /// Also owns the compile *session* routes (compile/request + compile/status): an external
    /// agent that edits scripts on disk can trigger an AssetDatabase.Refresh and then long-poll
    /// the session until the resulting compile finishes (or is found to be unnecessary). The
    /// session survives the success-path domain reload via SessionState.
    /// </summary>
    [InitializeOnLoad]
    public static class ConsoleCommands
    {
        static ConsoleCommands()
        {
            EnsureCompilationHook();
            RestoreCompileMessages();
            RestoreSession();
        }

        // ───────────────────────────── Console log reading (LogEntries) ─────────────────────────────

        // LogEntry.mode bit masks (stable UnityEditor ConsoleWindow.Mode values) — classify type.
        private const int ModeError =
            (1 << 0)  /*Error*/              | (1 << 1)  /*Assert*/            | (1 << 4)  /*Fatal*/ |
            (1 << 6)  /*AssetImportError*/   | (1 << 8)  /*ScriptingError*/    | (1 << 11) /*ScriptCompileError*/ |
            (1 << 13) /*StickyError*/        | (1 << 17) /*ScriptingException*/| (1 << 20) /*GraphCompileError*/  |
            (1 << 21) /*ScriptingAssertion*/ | (1 << 22) /*VisualScriptingError*/;
        private const int ModeWarning =
            (1 << 7)  /*AssetImportWarning*/ | (1 << 9)  /*ScriptingWarning*/  | (1 << 12) /*ScriptCompileWarning*/;

        // Cap how many of the most-recent rows we walk, to bound reflection cost under heavy spam.
        private const int ScanLimit = 5000;

        private static Type _logEntriesType, _logEntryType;
        private static MethodInfo _startGettingEntries, _endGettingEntries, _getCount, _getEntryInternal, _getEntryCount, _clearMethod;
        private static FieldInfo _messageField, _modeField, _callstackStartField;
        private static bool _reflectionResolved, _reflectionOk;
        private static string _bindError;

        private const string FixHint =
            "Fix the reflection bindings in plugin/Editor/Commands/ConsoleCommands.cs (ResolveReflection) for this Unity version.";

        private static void ResolveReflection()
        {
            if (_reflectionResolved) return;
            _reflectionResolved = true;

            // Track exactly which members fail to bind, so an engine upgrade that renames/moves the
            // internal console API produces an error naming the precise culprit(s) — not a vague failure.
            var missing = new List<string>();
            try
            {
                _logEntriesType = Type.GetType("UnityEditor.LogEntries,UnityEditor")
                    ?? Type.GetType("UnityEditor.LogEntries,UnityEditor.CoreModule");
                _logEntryType = Type.GetType("UnityEditor.LogEntry,UnityEditor")
                    ?? Type.GetType("UnityEditor.LogEntry,UnityEditor.CoreModule");
                if (_logEntriesType is null) missing.Add("type UnityEditor.LogEntries");
                if (_logEntryType is null) missing.Add("type UnityEditor.LogEntry");

                const BindingFlags S = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                const BindingFlags I = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                if (_logEntriesType is not null)
                {
                    _startGettingEntries = _logEntriesType.GetMethod("StartGettingEntries", S);
                    _endGettingEntries = _logEntriesType.GetMethod("EndGettingEntries", S);
                    _getEntryInternal = _logEntriesType.GetMethod("GetEntryInternal", S);
                    _getCount = _logEntriesType.GetMethod("GetCount", S);          // optional: StartGettingEntries also returns the count
                    _getEntryCount = _logEntriesType.GetMethod("GetEntryCount", S); // optional: per-row repeat (collapse) count
                    _clearMethod = _logEntriesType.GetMethod("Clear", S);           // used by console/clear only

                    if (_startGettingEntries is null) missing.Add("LogEntries.StartGettingEntries()");
                    if (_endGettingEntries is null) missing.Add("LogEntries.EndGettingEntries()");
                    if (_getEntryInternal is null) missing.Add("LogEntries.GetEntryInternal(int, LogEntry)");
                }

                if (_logEntryType is not null)
                {
                    _messageField = _logEntryType.GetField("message", I) ?? _logEntryType.GetField("condition", I);
                    _modeField = _logEntryType.GetField("mode", I);
                    // Optional: the UTF-16 char offset where the stack trace begins within `message`. Present on
                    // Unity 6.x; when bound we split there so a multi-line user message stays intact (rather than
                    // misclassifying its 2nd+ lines as stack trace). Absent → first-newline fallback in SplitMessage.
                    _callstackStartField = _logEntryType.GetField("callstackTextStartUTF16", I);
                    if (_messageField is null) missing.Add("LogEntry.message (or .condition)");
                    if (_modeField is null) missing.Add("LogEntry.mode");
                }

                _reflectionOk = missing.Count == 0;
                if (!_reflectionOk)
                    _bindError =
                        $"Unity console reflection failed on Unity {Application.unityVersion}: the internal " +
                        $"UnityEditor.LogEntries/LogEntry API changed — could not bind [{string.Join(", ", missing)}]. " + FixHint;
            }
            catch (Exception ex)
            {
                _reflectionOk = false;
                _bindError = $"Unity console reflection threw on Unity {Application.unityVersion}: " +
                             $"{ex.GetType().Name}: {ex.Message}. " + FixHint;
            }
        }

        // Helpful error when an Invoke fails at runtime (member bound but its signature/behaviour changed).
        private static object ReadFailure(string stage, Exception ex) => new
        {
            error = $"Unity console read failed while {stage} on Unity {Application.unityVersion}: " +
                    $"{ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}. " +
                    "The internal UnityEditor.LogEntries API signature likely changed. " + FixHint,
        };

        private static string ClassifyType(int mode) =>
            (mode & ModeError) != 0 ? "error" : (mode & ModeWarning) != 0 ? "warning" : "log";

        // Unity stores a log as "<user message>\n<stack trace>" in one field. Split into the (possibly multi-line)
        // user message and the stack. Prefers Unity's own callstack offset so a multi-line message survives intact;
        // falls back to the first newline when that field isn't bound (older Unity) — which misclassifies a
        // multi-line message's 2nd+ lines as stack trace, the reason the offset is preferred.
        private static void SplitMessage(object entry, string full, out string message, out string stack)
        {
            if (_callstackStartField is not null)
            {
                int cs = Convert.ToInt32(_callstackStartField.GetValue(entry));
                if (cs > 0 && cs <= full.Length)
                {
                    message = full.Substring(0, cs).TrimEnd('\n', '\r');
                    stack = full.Substring(cs);
                    return;
                }
            }
            int nl = full.IndexOf('\n');
            message = nl >= 0 ? full.Substring(0, nl) : full;
            stack = nl >= 0 ? full.Substring(nl + 1) : "";
        }

        private sealed class LogAgg
        {
            public string Condition;
            public string Type;
            public string Full;
            public string Stack;
            public int Count;
            public int LastIndex;
        }

        [McpRoute("console/log",
            "Read the Unity Console. Reflects the Console's current type toggles + search filter. " +
            "Args: collapse (default true — dedupe identical messages with a repeat count), count (max returned, default 50), " +
            "type (all|error|warning|info), match (substring), regex (treat match as regex), includeStackTrace (default false).")]
        public static object GetLog(JObject args)
        {
            ResolveReflection();
            if (!_reflectionOk) return new { error = _bindError };

            int max = Math.Max(1, args.Value<int?>("count") ?? 50);
            bool collapse = args.Value<bool?>("collapse") ?? true;
            string typeFilter = (args.Value<string>("type") ?? "all").ToLowerInvariant();
            string match = args.Value<string>("match");
            bool useRegex = args.Value<bool?>("regex") ?? false;
            bool includeStack = args.Value<bool?>("includeStackTrace") ?? false;

            Regex re = null;
            if (useRegex && !string.IsNullOrEmpty(match))
            {
                try { re = new Regex(match, RegexOptions.IgnoreCase); }
                catch (Exception ex) { return new { error = $"Invalid regex: {ex.Message}" }; }
            }

            var entry = Activator.CreateInstance(_logEntryType);
            var byKey = new Dictionary<string, LogAgg>();
            var raw = new List<LogAgg>();

            int total;
            try
            {
                object sge = _startGettingEntries.Invoke(null, null);
                total = sge is int n ? n : (_getCount is not null ? (int)_getCount.Invoke(null, null) : 0);
            }
            catch (Exception ex) { return ReadFailure("starting the console read", ex); }

            int start = Math.Max(0, total - ScanLimit);
            bool scanTruncated = start > 0;

            try
            {
                var entryBox = new object[] { 0, entry };
                var rowBox = new object[1];
                for (int i = start; i < total; i++)
                {
                    entryBox[0] = i;
                    bool ok = (bool)_getEntryInternal.Invoke(null, entryBox);
                    if (!ok) continue;

                    string full = _messageField.GetValue(entry) as string ?? "";
                    int mode = Convert.ToInt32(_modeField.GetValue(entry));
                    int repeat = 1;
                    if (_getEntryCount is not null) { rowBox[0] = i; repeat = (int)_getEntryCount.Invoke(null, rowBox); }

                    string type = ClassifyType(mode);
                    if (typeFilter == "error" && type != "error") continue;
                    if (typeFilter == "warning" && type != "warning") continue;
                    if (typeFilter == "info" && type != "log") continue;

                    SplitMessage(entry, full, out string condition, out string stack);

                    if (re is not null) { if (!re.IsMatch(condition)) continue; }
                    else if (!string.IsNullOrEmpty(match) && condition.IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    if (collapse)
                    {
                        if (!byKey.TryGetValue(full, out var agg))
                        {
                            agg = new LogAgg { Condition = condition, Type = type, Full = full, Stack = stack, Count = 0, LastIndex = i };
                            byKey[full] = agg;
                        }
                        agg.Count += repeat;
                        agg.LastIndex = i;
                    }
                    else
                    {
                        raw.Add(new LogAgg { Condition = condition, Type = type, Full = full, Stack = stack, Count = repeat, LastIndex = i });
                    }
                }
            }
            catch (Exception ex)
            {
                return ReadFailure("reading console entries", ex);
            }
            finally
            {
                try { _endGettingEntries.Invoke(null, null); } catch { /* best-effort release */ }
            }

            List<LogAgg> pool = collapse ? new List<LogAgg>(byKey.Values) : raw;
            int matchedTotal = pool.Count;
            pool.Sort((a, b) => a.LastIndex.CompareTo(b.LastIndex)); // chronological
            List<LogAgg> selected = pool.Count > max ? pool.GetRange(pool.Count - max, max) : pool;

            var entries = new List<object>();
            foreach (var a in selected)
            {
                var d = new Dictionary<string, object>
                {
                    { "message", a.Condition },
                    { "type", a.Type },
                    { "count", a.Count },
                };
                if (includeStack)
                    d["stackTrace"] = a.Stack;
                entries.Add(d);
            }

            return new Dictionary<string, object>
            {
                { "collapsed", collapse },
                { collapse ? "uniqueTotal" : "matchedTotal", matchedTotal },
                { "returned", entries.Count },
                { "scannedRows", total - start },
                { "scanTruncated", scanTruncated },
                { "entries", entries },
            };
        }

        [McpRoute("console/clear", "Clear the Unity Console (clears Unity's actual console store).")]
        public static object Clear(JObject args)
        {
            ResolveReflection();
            if (!_reflectionOk) return new { error = _bindError };
            if (_clearMethod is null)
                return new { error = $"LogEntries.Clear() not found on Unity {Application.unityVersion}. " + FixHint };
            try
            {
                _clearMethod.Invoke(null, null);
                return new { success = true, message = "Unity console cleared." };
            }
            catch (Exception ex) { return ReadFailure("clearing the console", ex); }
        }

        // ──────────────────────────── Compilation diagnostics (CompilationPipeline) ────────────────────────────

        private struct CompileMessage
        {
            public string File;
            public int Line;
            public int Column;
            public string Message;
            public string Severity; // "error" | "warning"
            public string Assembly;
            public DateTime Time;
        }

        // Persisted to SessionState so warnings survive the successful-compile domain reload.
        private const string CompileStateKey = "Adanub_UnityMcp_CompileMessages";
        private static readonly List<CompileMessage> _compileMessages = new List<CompileMessage>();
        private static bool _compileHooked;

        private static void EnsureCompilationHook()
        {
            if (_compileHooked) return;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReloadSession;
            EditorApplication.update += OnEditorTickSession;
            _compileHooked = true;
        }

        private static void OnCompilationStarted(object context)
        {
            lock (_compileMessages) _compileMessages.Clear();
            PersistCompileMessages();

            // Only adopt a compile that began after the session's own refresh ran — a compile
            // already in flight when the session started would otherwise be mistaken for ours,
            // finishing the session with its result while the queued refresh never executes.
            if (_refreshDone && (_sessionPhase == PhaseRefreshQueued || _sessionPhase == PhaseWaitingForCompile))
            {
                _sessionPhase = PhaseCompiling;
                PersistSession();
            }
        }

        private static void OnAssemblyCompiled(string assemblyPath, CompilerMessage[] messages)
        {
            string asm = Path.GetFileNameWithoutExtension(assemblyPath);
            lock (_compileMessages)
            {
                foreach (var m in messages)
                {
                    if (m.type != CompilerMessageType.Error && m.type != CompilerMessageType.Warning) continue;
                    _compileMessages.Add(new CompileMessage
                    {
                        File = m.file ?? "",
                        Line = m.line,
                        Column = m.column,
                        Message = m.message ?? "",
                        Severity = m.type == CompilerMessageType.Error ? "error" : "warning",
                        Assembly = asm,
                        Time = DateTime.Now,
                    });
                }
            }
            PersistCompileMessages();
        }

        private static void PersistCompileMessages()
        {
            try
            {
                string json;
                lock (_compileMessages) json = JsonConvert.SerializeObject(_compileMessages);
                SessionState.SetString(CompileStateKey, json);
            }
            catch { /* best-effort */ }
        }

        private static void RestoreCompileMessages()
        {
            string json = SessionState.GetString(CompileStateKey, "");
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                var restored = JsonConvert.DeserializeObject<List<CompileMessage>>(json);
                if (restored is not null)
                    lock (_compileMessages) { _compileMessages.Clear(); _compileMessages.AddRange(restored); }
            }
            catch { /* ignore malformed persisted state */ }
        }

        [McpRoute("compilation/errors", "Compiler errors/warnings from the last compile (survives the domain reload). Args: count (50), severity (all|error|warning).")]
        public static object GetCompilationErrors(JObject args)
        {
            EnsureCompilationHook();
            int count = args.Value<int?>("count") ?? 50;
            string severity = (args.Value<string>("severity") ?? "all").ToLowerInvariant();

            var picked = new List<object>();
            lock (_compileMessages)
            {
                for (int i = _compileMessages.Count - 1; i >= 0 && picked.Count < count; i--)
                {
                    var m = _compileMessages[i];
                    if (severity != "all" && m.Severity != severity) continue;
                    picked.Add(new Dictionary<string, object>
                    {
                        { "file", m.File },
                        { "line", m.Line },
                        { "column", m.Column },
                        { "message", m.Message },
                        { "severity", m.Severity },
                        { "assembly", m.Assembly },
                        { "time", m.Time.ToString("HH:mm:ss.fff") },
                    });
                }
            }
            picked.Reverse();
            return new Dictionary<string, object>
            {
                { "count", picked.Count },
                { "isCompiling", EditorApplication.isCompiling },
                { "entries", picked },
            };
        }

        // ──────────────────────────── Compile session (compile/request + compile/status) ────────────────────────────
        //
        // Phases: idle → refreshQueued → waitingForCompile → compiling → finished.
        // Results (only when finished): clean | errors | noCompile.
        //
        // The refresh is deferred by a couple of EditorApplication.update ticks so the HTTP
        // response for compile/request is flushed before the (synchronous) refresh/import work
        // starts. NOT delayCall: an unfocused editor services update but can defer delayCall
        // indefinitely (it rides the GUI/inspector cycle), and triggering a compile without
        // having to focus the editor is the whole point of this route. On a clean compile Unity
        // reloads the domain; the session survives via SessionState and is restored as
        // finished/clean with domainReloaded=true.

        private const string PhaseIdle = "idle";
        private const string PhaseRefreshQueued = "refreshQueued";
        private const string PhaseWaitingForCompile = "waitingForCompile";
        private const string PhaseCompiling = "compiling";
        private const string PhaseFinished = "finished";

        private const string ResultClean = "clean";
        private const string ResultErrors = "errors";
        private const string ResultNoCompile = "noCompile";

        // Quiet time (no compiling, no importing) after the refresh before concluding that no
        // compile was needed. Activity restarts the timer, so slow imports don't cause a false
        // noCompile while a script compile is still queued behind them.
        private const double NoCompileGraceSeconds = 2.0;

        private const string SessionKey = "Adanub_UnityMcp_CompileSession";

        private static int _sessionId;
        private static string _sessionPhase = PhaseIdle;
        private static string _sessionResult;      // null until finished
        private static bool _sessionDomainReloaded;
        private static double _sessionQuietSince;  // timeSinceStartup when the grace timer (re)started
        private static int _refreshTicksRemaining; // >0: update ticks until the deferred refresh runs
        private static bool _refreshDone;          // the current session's AssetDatabase.Refresh has executed

        [Serializable]
        private sealed class SessionSnapshot
        {
            public int Id;
            public string Phase;
            public string Result;
            public bool ReloadPending;
            public bool DomainReloaded;
        }

        private static void PersistSession(bool reloadPending = false)
        {
            try
            {
                SessionState.SetString(SessionKey, JsonConvert.SerializeObject(new SessionSnapshot
                {
                    Id = _sessionId,
                    Phase = _sessionPhase,
                    Result = _sessionResult,
                    ReloadPending = reloadPending,
                    DomainReloaded = _sessionDomainReloaded,
                }));
            }
            catch { /* best-effort */ }
        }

        private static void OnBeforeAssemblyReloadSession()
        {
            if (_sessionId == 0 || _sessionPhase == PhaseIdle) return;

            // ReloadPending marks a reload the session must resolve on restore: its own
            // clean-compile reload (finished/clean not yet flagged) or a reload that pre-empted
            // it mid-flight. Already-resolved sessions (errors / noCompile / clean-and-flagged)
            // persist as-is so a later unrelated reload can't rewrite their result.
            bool awaitingOwnReload =
                _sessionPhase == PhaseFinished && _sessionResult == ResultClean && !_sessionDomainReloaded;
            PersistSession(reloadPending: awaitingOwnReload || _sessionPhase != PhaseFinished);
        }

        private static void RestoreSession()
        {
            string json = SessionState.GetString(SessionKey, "");
            if (string.IsNullOrEmpty(json)) return;

            SessionSnapshot snap;
            try { snap = JsonConvert.DeserializeObject<SessionSnapshot>(json); }
            catch { return; }
            if (snap is null) return;

            _sessionId = snap.Id;
            _sessionDomainReloaded = snap.DomainReloaded;

            if (!snap.ReloadPending)
            {
                _sessionPhase = snap.Phase ?? PhaseIdle;
                _sessionResult = snap.Result;
                return;
            }

            switch (snap.Phase)
            {
                case PhaseRefreshQueued:
                case PhaseWaitingForCompile:
                    // The reload pre-empted the session before its compile began (play-mode
                    // entry, user-triggered reload, ...). Re-arm the refresh in the new domain
                    // rather than reporting a compile that never happened.
                    _sessionPhase = PhaseRefreshQueued;
                    _sessionResult = null;
                    _refreshDone = false;
                    _refreshTicksRemaining = 2;
                    break;

                default:
                    // compiling/finished: the reload is the clean compile's own assembly swap
                    // (compilationFinished fires before the reload, so compiling here is the
                    // rare missed-event case — the swap still means the compile succeeded).
                    _sessionPhase = PhaseFinished;
                    _sessionResult = snap.Result ?? ResultClean;
                    _sessionDomainReloaded = true;
                    break;
            }
            PersistSession();
        }

        private static void OnCompilationFinished(object context)
        {
            // Only compiling — pre-session compiles must not finish the session (their start was
            // rejected above, so their finish must be too).
            if (_sessionPhase != PhaseCompiling) return;

            bool anyErrors;
            lock (_compileMessages) anyErrors = _compileMessages.Exists(m => m.Severity == "error");
            _sessionPhase = PhaseFinished;
            _sessionResult = anyErrors ? ResultErrors : ResultClean;
            PersistSession();
        }

        private static void OnEditorTickSession()
        {
            if (_refreshTicksRemaining > 0)
            {
                _refreshTicksRemaining--;
                if (_refreshTicksRemaining == 0) DoScheduledRefresh();
            }

            if (_sessionPhase != PhaseWaitingForCompile) return;

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                _sessionQuietSince = EditorApplication.timeSinceStartup;
                return;
            }

            if (EditorApplication.timeSinceStartup - _sessionQuietSince >= NoCompileGraceSeconds)
            {
                _sessionPhase = PhaseFinished;
                _sessionResult = ResultNoCompile;
                PersistSession();
            }
        }

        private static void DoScheduledRefresh()
        {
            if (_sessionPhase != PhaseRefreshQueued) return; // superseded, or compile already started

            _refreshDone = true; // before Refresh — compilationStarted can fire inside it
            AssetDatabase.Refresh();

            if (_sessionPhase == PhaseRefreshQueued) // compilationStarted may have fired inside Refresh
            {
                _sessionPhase = PhaseWaitingForCompile;
                _sessionQuietSince = EditorApplication.timeSinceStartup;
                PersistSession();
            }
        }

        [McpRoute("compile/request",
            "Trigger an AssetDatabase.Refresh so the editor picks up script changes made on disk and compiles them. " +
            "Returns immediately; poll compile/status (use waitMs to long-poll) until phase=finished.")]
        public static object RequestCompile(JObject args)
        {
            EnsureCompilationHook();

            _sessionId++;
            _sessionPhase = PhaseRefreshQueued;
            _sessionResult = null;
            _sessionDomainReloaded = false;
            _refreshDone = false;
            PersistSession();

            // Two update ticks ≈ enough for the request thread to flush the HTTP response;
            // see the deferral note at the top of this region for why this is not delayCall.
            _refreshTicksRemaining = 2;

            return new Dictionary<string, object>
            {
                { "sessionId", _sessionId },
                { "phase", _sessionPhase },
                { "isPlaying", EditorApplication.isPlaying },
            };
        }

        // Main-thread snapshot for the request-thread long-poll. Returned as a 2-element array
        // (phase, payload) so the caller can distinguish a real snapshot from the anonymous
        // error object RunOnMainThread substitutes on timeout/exception — matching by shape is
        // airtight where a captured-local sentinel is not (a late-running or throwing lambda
        // can leave a stale/partial local behind).
        internal static object[] SnapshotStatus(int maxMessages)
        {
            return new object[] { _sessionPhase, BuildStatusPayload(maxMessages) };
        }

        internal const string PhaseFinishedValue = PhaseFinished;
        internal const string PhaseIdleValue = PhaseIdle;

        // Main thread only.
        private static object BuildStatusPayload(int maxMessages)
        {
            int errors = 0, warnings = 0;
            var messages = new List<object>();
            lock (_compileMessages)
            {
                foreach (var m in _compileMessages)
                {
                    if (m.Severity == "error") errors++;
                    else warnings++;
                }

                for (int i = Math.Max(0, _compileMessages.Count - maxMessages); i < _compileMessages.Count; i++)
                {
                    var m = _compileMessages[i];
                    messages.Add(new Dictionary<string, object>
                    {
                        { "file", m.File },
                        { "line", m.Line },
                        { "column", m.Column },
                        { "message", m.Message },
                        { "severity", m.Severity },
                        { "assembly", m.Assembly },
                    });
                }
            }

            return new Dictionary<string, object>
            {
                { "sessionId", _sessionId },
                { "phase", _sessionPhase },
                { "result", _sessionResult },
                { "domainReloaded", _sessionDomainReloaded },
                { "isCompiling", EditorApplication.isCompiling },
                { "isUpdating", EditorApplication.isUpdating },
                { "isPlaying", EditorApplication.isPlaying },
                { "errorCount", errors },
                { "warningCount", warnings },
                { "messages", messages },
            };
        }
    }

    /// <summary>
    /// Long-polling compile/status route. Lives in its own type with no static state because
    /// RunOnRequestThread handlers are invoked on a ThreadPool thread, which would run the
    /// declaring type's static initialiser there — for <see cref="ConsoleCommands"/> that means
    /// SessionState reads off the main thread during the editor's [InitializeOnLoad] window.
    /// All ConsoleCommands access happens inside the main-thread hop instead.
    /// </summary>
    public static class CompileStatusRoute
    {
        [McpRoute("compile/status",
            "Status of the compile session started by compile/request. Args: waitMs (0-25000 — long-poll until " +
            "finished or the wait elapses), count (max messages, default 50). On a clean compile the domain reload " +
            "briefly drops the bridge connection — retry the call and it will report finished.",
            RunOnRequestThread = true)]
        public static object GetCompileStatus(JObject args)
        {
            int waitMs = Math.Clamp(args.Value<int?>("waitMs") ?? 0, 0, 25_000);
            int maxMessages = Math.Max(1, args.Value<int?>("count") ?? 50);

            var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
            while (true)
            {
                object result = McpBridgeServer.RunOnMainThread(() => ConsoleCommands.SnapshotStatus(maxMessages));
                if (result is not object[] snap)
                    return result; // hop timed out or threw; result is the error object

                var phase = (string)snap[0];
                object payload = snap[1];
                if (phase == ConsoleCommands.PhaseFinishedValue ||
                    phase == ConsoleCommands.PhaseIdleValue ||
                    DateTime.UtcNow >= deadline)
                    return payload;

                Thread.Sleep(250);
            }
        }
    }
}
