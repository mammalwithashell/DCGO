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

        /// <summary>
        /// Harness root holding jobs/ claimed/ done/ failed/. Defaults beside
        /// the recorder's output so both live under persistentDataPath.
        /// </summary>
        public static string Root { get; set; } =
            Path.Combine(Application.persistentDataPath, "dcgo_harness");

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
