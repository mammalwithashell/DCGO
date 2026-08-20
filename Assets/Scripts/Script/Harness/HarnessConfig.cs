using System.IO;
using UnityEngine;

namespace Digimon.Harness
{
    /// <summary>
    /// Configuration for the unattended job harness. Mirrors
    /// <see cref="Digimon.Recording.RecorderConfig"/>'s shape so both mods are
    /// configured the same way.
    /// </summary>
    public static class HarnessConfig
    {
        /// <summary>
        /// Master switch. When false the JobWatcher never bootstraps and DCGO
        /// behaves exactly as upstream. Default OFF so a normal play session is
        /// never hijacked by a stale job file.
        /// </summary>
        private static bool _enabled;

        /// <summary>Backing key shared with the Editor menu toggle.</summary>
        public const string EnabledPrefKey = "Digimon.Harness.Enabled";

        /// <summary>
        /// Presence of this file enables the harness. Created/removed by
        /// `dcgo-harness enable` / `disable`.
        /// </summary>
        public static string EnabledMarkerPath => Path.Combine(Root, "harness.enabled");

        public static bool Enabled
        {
            get
            {
                // A marker file in the harness root is the primary switch. It
                // needs no Editor code, works in a player build, and is set by
                // the same CLI that queues the jobs -- so enabling cannot depend
                // on a Unity menu registering correctly. Still explicit: an
                // operator has to run `dcgo-harness enable`, so a stale job file
                // alone can never hijack a normal play session.
                try
                {
                    if (File.Exists(EnabledMarkerPath)) return true;
                }
                catch (System.Exception)
                {
                    // An unreadable root is not a reason to start a batch.
                }
#if UNITY_EDITOR
                // Read the operator's choice straight from EditorPrefs rather
                // than trusting a static field. Entering Play mode triggers a
                // domain reload that wipes all static state, so a value set by
                // the menu beforehand is gone by the time the runtime bootstrap
                // reads it -- the harness would silently do nothing. EditorPrefs
                // survives the reload, so there is no ordering dependency at all.
                return UnityEditor.EditorPrefs.GetBool(EnabledPrefKey, false);
#else
                return _enabled;
#endif
            }
            set
            {
                _enabled = value;
#if UNITY_EDITOR
                UnityEditor.EditorPrefs.SetBool(EnabledPrefKey, value);
#endif
            }
        }

        private static string _root;

        /// <summary>
        /// Harness root holding jobs/ claimed/ done/ failed/. Defaults beside
        /// the recorder's output so both live under persistentDataPath.
        /// </summary>
        /// <remarks>
        /// The default is computed on read rather than baked into a static
        /// initializer, mirroring <c>RecorderConfig.DefaultOutputDirectory</c>.
        /// Unity restricts when <see cref="Application.persistentDataPath"/> may
        /// be touched, and a static initializer runs at whatever arbitrary moment
        /// the type is first used — which for this class is a
        /// [RuntimeInitializeOnLoadMethod] bootstrap. Reading it lazily keeps the
        /// call inside a normal frame instead.
        /// </remarks>
        public static string Root
        {
            get => string.IsNullOrEmpty(_root)
                ? Path.Combine(Application.persistentDataPath, "dcgo_harness")
                : _root;
            set => _root = value;
        }

        /// <summary>
        /// Time multiplier while a job runs. A corpus of hundreds of games is
        /// worthless if each spends 40s in animation. Raised, not unbounded:
        /// very high scales can starve coroutines that yield per-frame.
        /// </summary>
        public static float TimeScale { get; set; } = 8f;

        /// <summary>
        /// File the watcher touches every poll so the host can tell a working
        /// DCGO from a hung one.
        /// </summary>
        /// <remarks>
        /// A PID is not enough: a hung Unity keeps its process alive and reports
        /// healthy forever. Both failures actually hit so far -- the unleft
        /// Photon room and the stalled selection -- looked exactly like that.
        /// The heartbeat is touched from the poll loop rather than from job
        /// completion, so it keeps advancing during a long game but stops if the
        /// coroutine itself dies.
        /// </remarks>
        public static string HeartbeatPath => Path.Combine(Root, "harness.heartbeat");

        /// <summary>
        /// Quit after this many seconds with nothing to do. 0 disables it.
        /// </summary>
        /// <remarks>
        /// One knob serves both lifecycles: a one-shot subprocess sets it low so
        /// it terminates when the queue drains; the warm daemon sets it high or
        /// leaves it off. Default 0 so Editor sessions are unaffected -- an
        /// Editor that exits Play mode on its own would be baffling.
        /// </remarks>
        public static float ExitAfterIdleSeconds { get; set; }

        /// <summary>How often the watcher looks for new jobs.</summary>
        public static float PollSeconds { get; set; } = 1f;

        public static string JobsDir => Path.Combine(Root, "jobs");
        public static string ClaimedDir => Path.Combine(Root, "claimed");
        public static string DoneDir => Path.Combine(Root, "done");
        public static string FailedDir => Path.Combine(Root, "failed");
    }
}
