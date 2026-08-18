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
        public static bool Enabled { get; set; } = false;

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

        /// <summary>How often the watcher looks for new jobs.</summary>
        public static float PollSeconds { get; set; } = 1f;

        public static string JobsDir => Path.Combine(Root, "jobs");
        public static string ClaimedDir => Path.Combine(Root, "claimed");
        public static string DoneDir => Path.Combine(Root, "done");
        public static string FailedDir => Path.Combine(Root, "failed");
    }
}
