using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>
    /// The editor's play-mode state as <see cref="EditorApplication.playModeStateChanged"/> reports
    /// it, held where a request thread can WAIT on it: a waiter blocks on the lock's monitor and the
    /// event wakes it, so a transition is answered the moment it completes and nothing is polled.
    /// Written only on the main thread: the static constructor, which <see cref="McpBridgeServer"/>
    /// runs explicitly before it starts listening (a request thread must never be the first to touch
    /// this type), and the event handler.
    /// </summary>
    [InitializeOnLoad]
    internal static class PlayModeTransitions
    {
        // SessionState survives a domain reload, so a domain loaded mid-session (a play entry's
        // reload, a recompile while playing) resumes from what the previous domain heard.
        private const string EditEntriesKey = "Adanub.UnityMcp.PlayModeTransitions.EditEntries";
        private const string EnteredPlayKey = "Adanub.UnityMcp.PlayModeTransitions.EnteredPlay";
        private const string LastEventKey = "Adanub.UnityMcp.PlayModeTransitions.LastEvent";

        private enum State
        {
            Edit,
            Play,
            Transitioning,
        }

        private static readonly object Gate = new();
        private static State s_state;
        private static string s_lastEvent;

        // Every return to edit mode this editor session — each EnteredEditMode, and each cancelled
        // play entry, which delivers no EnteredEditMode (below): a play request records it, and a
        // wait for play that sees it move knows the entry ended back in edit mode rather than
        // waiting out its bound.
        private static int s_editEntries;

        static PlayModeTransitions()
        {
            EditorApplication.playModeStateChanged -= OnChanged;
            EditorApplication.playModeStateChanged += OnChanged;
            lock (Gate)
            {
                s_editEntries = SessionState.GetInt(EditEntriesKey, 0);
                s_lastEvent = SessionState.GetString(LastEventKey, null);
                // isPlaying already reads true in the reload a play entry makes, before
                // EnteredPlayMode: only an entry this session heard complete counts as play.
                var enteredPlay = SessionState.GetBool(EnteredPlayKey, false);
                s_state = EditorApplication.isPlaying && enteredPlay ? State.Play
                    : EditorApplication.isPlayingOrWillChangePlaymode ? State.Transitioning
                    : State.Edit;
            }
        }

        /// <summary>The count of returns to edit mode, recorded by a play request for <see cref="WaitFor"/>.</summary>
        internal static int EditEntries
        {
            get
            {
                lock (Gate)
                    return s_editEntries;
            }
        }

        // A play entry cancelled from an ExitingEditMode handler (EditorApplication.isPlaying set
        // back to false — how a pre-play validation refuses) delivers ExitingEditMode, then
        // ExitingPlayMode with isPlaying already false, and neither Entered event: that
        // ExitingPlayMode is the return to edit mode. A real exit's ExitingPlayMode reads isPlaying true.
        private static void OnChanged(PlayModeStateChange change)
        {
            lock (Gate)
            {
                s_lastEvent = change.ToString();
                SessionState.SetString(LastEventKey, s_lastEvent);
                var entryCancelled = change == PlayModeStateChange.ExitingPlayMode && !EditorApplication.isPlaying;
                s_state = change switch
                {
                    PlayModeStateChange.EnteredPlayMode => State.Play,
                    PlayModeStateChange.EnteredEditMode => State.Edit,
                    _ when entryCancelled => State.Edit,
                    _ => State.Transitioning,
                };
                SessionState.SetBool(EnteredPlayKey, change == PlayModeStateChange.EnteredPlayMode);
                if (change == PlayModeStateChange.EnteredEditMode || entryCancelled)
                {
                    s_editEntries++;
                    SessionState.SetInt(EditEntriesKey, s_editEntries);
                }
                Monitor.PulseAll(Gate);
            }
        }

        /// <summary>
        /// Blocks until the editor is settled in play mode (<paramref name="play"/>) or edit mode,
        /// or — waiting for play with <paramref name="editEntriesAtRequest"/> given — until a return
        /// to edit mode after that request (an EnteredEditMode, or a cancelled entry) says the entry
        /// did not happen; <paramref name="waitMs"/>
        /// bounds a transition that never completes. Call from a request thread only: it blocks.
        /// </summary>
        internal static Dictionary<string, object> WaitFor(bool play, int? editEntriesAtRequest, int waitMs)
        {
            var target = play ? State.Play : State.Edit;
            var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
            lock (Gate)
            {
                while (true)
                {
                    if (s_state == target)
                        return Result(true, null, null);
                    if (play && editEntriesAtRequest is { } atRequest && s_editEntries != atRequest)
                        return Result(false, "edit", "The play entry ended back in edit mode: refused by an ExitingEditMode " +
                                                     "handler (a pre-play validation) or stopped. Read the console.");
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                        return Result(false, null, $"No {(play ? "EnteredPlayMode" : "EnteredEditMode")} within {waitMs} ms.");
                    Monitor.Wait(Gate, remaining);
                }
            }
        }

        private static Dictionary<string, object> Result(bool settled, string ended, string note)
        {
            var result = new Dictionary<string, object>
            {
                { "settled", settled },
                { "state", s_state.ToString().ToLowerInvariant() },
                { "lastEvent", s_lastEvent },
            };
            if (ended is not null)
                result["ended"] = ended;
            if (note is not null)
                result["note"] = note;
            return result;
        }
    }

    /// <summary>
    /// The long-waiting half of play-mode control. Its own type with no static state; the state lives
    /// in <see cref="PlayModeTransitions"/>, initialised on the main thread before the bridge listens.
    /// </summary>
    public static class PlayModeWaitRoute
    {
        [McpRoute("editor/playmode-wait",
            "Blocks until a play-mode transition completes, woken by the editor's playModeStateChanged event (no polling). " +
            "Args: target (required) — 'play' or 'edit'; editEntries (optional, from editor/playmode's result) — with target " +
            "'play', returns as soon as the entry ends back in edit mode instead (ended: 'edit'); waitMs (0-25000, default " +
            "25000) bounds a transition that never completes. Returns settled, state (edit | play | transitioning), lastEvent, " +
            "and ended / note when not settled. A domain reload during the transition drops this request; re-issue it and it " +
            "answers from the reloaded editor's state.",
            RunOnRequestThread = true)]
        public static object Wait(JObject args)
        {
            var target = args.Value<string>("target")?.ToLowerInvariant();
            if (target is not ("play" or "edit"))
                return new { error = "'target' is required: play | edit." };
            var waitMs = Math.Clamp(args.Value<int?>("waitMs") ?? 25_000, 0, 25_000);
            return PlayModeTransitions.WaitFor(target == "play", args.Value<int?>("editEntries"), waitMs);
        }
    }
}
