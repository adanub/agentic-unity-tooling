using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
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
    /// </summary>
    [InitializeOnLoad]
    public static class ConsoleCommands
    {
        static ConsoleCommands()
        {
            EnsureCompilationHook();
            RestoreCompileMessages();
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
        private static FieldInfo _messageField, _modeField;
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

        private sealed class LogAgg
        {
            public string Condition;
            public string Type;
            public string Full;
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

                    int nl = full.IndexOf('\n');
                    string condition = nl >= 0 ? full.Substring(0, nl) : full;

                    if (re is not null) { if (!re.IsMatch(condition)) continue; }
                    else if (!string.IsNullOrEmpty(match) && condition.IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    if (collapse)
                    {
                        if (!byKey.TryGetValue(full, out var agg))
                        {
                            agg = new LogAgg { Condition = condition, Type = type, Full = full, Count = 0, LastIndex = i };
                            byKey[full] = agg;
                        }
                        agg.Count += repeat;
                        agg.LastIndex = i;
                    }
                    else
                    {
                        raw.Add(new LogAgg { Condition = condition, Type = type, Full = full, Count = repeat, LastIndex = i });
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
                {
                    int nl = a.Full.IndexOf('\n');
                    d["stackTrace"] = nl >= 0 ? a.Full.Substring(nl + 1) : "";
                }
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
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
            _compileHooked = true;
        }

        private static void OnCompilationStarted(object context)
        {
            lock (_compileMessages) _compileMessages.Clear();
            PersistCompileMessages();
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
    }
}
