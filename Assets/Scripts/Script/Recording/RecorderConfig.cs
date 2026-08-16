using System.IO;
using UnityEngine;

namespace Digimon.Recording
{
    /// <summary>
    /// Configuration for the DCGO game recorder. Read once at recorder
    /// bootstrap; defaults are wired for the Phase 1 bot-fuzzer loop.
    /// </summary>
    /// <remarks>
    /// Mutate via Inspector on the auto-created GameRecorder GameObject, or
    /// at runtime through <see cref="GameRecorder.Config"/> before the first
    /// game starts. Mid-game config changes are honored on the next game.
    /// </remarks>
    public sealed class RecorderConfig
    {
        /// <summary>
        /// Master enable switch. When false, all <c>GameRecorder.LogXxx</c>
        /// calls become no-ops and no files are written.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Enable recording for Bot Match games (where
        /// <c>GManager.instance.IsAI == true</c>). Defaults on — bot fuzzer
        /// is Phase 1's primary loop.
        /// </summary>
        public bool RecordBotMatches { get; set; } = true;

        /// <summary>
        /// Enable recording for PvP games (Random Match, Room Match).
        /// Defaults OFF: Phase 3 opt-in. User must explicitly enable.
        /// </summary>
        public bool RecordPvPMatches { get; set; } = false;

        /// <summary>
        /// Output directory for recordings. Defaults to
        /// <c>Application.persistentDataPath/dcgo_recordings/</c> so files
        /// land in a stable per-OS location:
        ///   Windows: %APPDATA%\..\LocalLow\&lt;company&gt;\&lt;product&gt;\dcgo_recordings
        ///   macOS:   ~/Library/Application Support/&lt;company&gt;/&lt;product&gt;/dcgo_recordings
        ///   Linux:   ~/.config/unity3d/&lt;company&gt;/&lt;product&gt;/dcgo_recordings
        /// </summary>
        public string OutputDirectory { get; set; }

        /// <summary>
        /// Flush the JSONL writer after this many rows. Smaller = less data
        /// lost on crash; larger = less I/O overhead. Phase 1 default of 16
        /// is a guess — tune based on observed bot-match cadence.
        /// </summary>
        public int FlushEveryNRows { get; set; } = 16;

        /// <summary>
        /// Upstream DCGO nulls the bot's attack decision in UNITY_EDITOR
        /// builds, making the editor bot a pacifist. True (default) keeps
        /// build parity so editor bot games exercise combat — required for a
        /// meaningful recording corpus. Set false to restore the upstream
        /// editor behaviour.
        /// </summary>
        public static bool KeepBotAttacksInEditor { get; set; } = true;

        /// <summary>
        /// Suppress the bot's mulligan (it redraws when it lacks a Level-3).
        /// Redraw reshuffles use RNG the replay harness cannot reproduce, so
        /// recorded games with a redraw cannot fully replay. Default true
        /// while the recording campaign runs; set false for build parity.
        /// </summary>
        public static bool SuppressBotMulligan { get; set; } = true;

        /// <summary>
        /// Cached default output directory (see <see cref="OutputDirectory"/>).
        /// </summary>
        public static string DefaultOutputDirectory =>
            Path.Combine(Application.persistentDataPath, "dcgo_recordings");

        /// <summary>
        /// Returns the resolved output directory, falling back to the
        /// platform default if not explicitly set.
        /// </summary>
        public string ResolvedOutputDirectory =>
            string.IsNullOrEmpty(OutputDirectory) ? DefaultOutputDirectory : OutputDirectory;
    }
}
