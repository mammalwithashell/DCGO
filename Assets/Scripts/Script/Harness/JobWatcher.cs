using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Digimon.Harness
{
    /// <summary>
    /// Polls the harness jobs directory, claims one job at a time, applies it,
    /// and starts a game. Replaces the blind BattleScene reload that
    /// <c>TurnStateMachine</c> performs at game end under plain auto mode with a
    /// job-driven one.
    /// </summary>
    public class JobWatcher : MonoBehaviour
    {
        public static JobWatcher Instance { get; private set; }

        /// <summary>The job currently being played, or null when idle.</summary>
        public HarnessJob CurrentJob { get; private set; }

        /// <summary>Path of the claimed job file, used when filing the result.</summary>
        public string ClaimedPath { get; private set; }

        public DateTime StartedUtc { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            if (!HarnessConfig.Enabled) return;
            if (Instance != null) return;

            var go = new GameObject("JobWatcher");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<JobWatcher>();
        }

        private void Start()
        {
            Directory.CreateDirectory(HarnessConfig.JobsDir);
            Directory.CreateDirectory(HarnessConfig.ClaimedDir);
            Directory.CreateDirectory(HarnessConfig.DoneDir);
            Directory.CreateDirectory(HarnessConfig.FailedDir);
            StartCoroutine(PollLoop());
        }

        private IEnumerator PollLoop()
        {
            while (true)
            {
                if (CurrentJob == null)
                {
                    TryClaimAndStart();
                }
                yield return new WaitForSecondsRealtime(HarnessConfig.PollSeconds);
            }
        }

        private void TryClaimAndStart()
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(HarnessConfig.JobsDir, "*.json");
            }
            catch (Exception e)
            {
                Debug.LogError("[Harness] listing jobs failed: " + e.Message);
                return;
            }
            if (files.Length == 0)
            {
                // [Harness mod - I3] Queue drained and no job is running (this
                // method only runs when CurrentJob == null): release harness
                // overrides so a game the user starts by hand afterward
                // doesn't inherit harness decks / an 8x time scale. See
                // ClearOverrides.
                ClearOverrides();
                return;
            }

            Array.Sort(files, StringComparer.Ordinal);
            string source = files[0];
            string claimed = Path.Combine(HarnessConfig.ClaimedDir, Path.GetFileName(source));

            // Atomic rename IS the claim. If it throws, another process (or a
            // stale handle) got there first; just try again next poll.
            try
            {
                File.Move(source, claimed);
            }
            catch (Exception)
            {
                return;
            }

            // [Harness mod - I4] DeckBuilder.FromCardIds and DeckData throw on
            // a few malformed-input paths that aren't guarded locally (see
            // DeckBuilder.FromCardIds). An uncaught exception here would
            // propagate out of TryClaimAndStart into the PollLoop coroutine,
            // which Unity then stops silently -- the job orphans in claimed/
            // and the harness never claims another one. Route any throw to
            // Fail like every other rejection path instead.
            HarnessJob job;
            try
            {
                job = HarnessJob.Parse(SafeRead(claimed));
                if (job == null)
                {
                    Fail(claimed, "unparseable job file");
                    return;
                }

                if (!ApplyJob(job))
                {
                    Fail(claimed, "could not apply job (deck resolution failed)");
                    return;
                }
            }
            catch (Exception e)
            {
                Fail(claimed, "exception applying job: " + e.Message);
                return;
            }

            CurrentJob = job;
            ClaimedPath = claimed;
            StartedUtc = DateTime.UtcNow;
            Debug.Log("[Harness] started job " + job.job_id);

            // Same handoff the auto-mode restart uses.
            ContinuousController.instance.isAI = true;
            SceneManager.LoadScene("BattleScene");
        }

        /// <summary>
        /// Configure the game from the job: decks, seed, auto mode, time scale.
        /// </summary>
        private bool ApplyJob(HarnessJob job)
        {
            DeckData p0 = DeckBuilder.FromCardIds("harness-p0", job.decks.p0);
            DeckData p1 = DeckBuilder.FromCardIds("harness-p1", job.decks.p1);
            if (p0 == null || p1 == null) return false;

            CardObjectController.HarnessDeckOverrideP0 = p0;
            CardObjectController.HarnessDeckOverrideP1 = p1;
            // [Harness mod - I3] Deliberately NOT setting
            // ContinuousController.instance.BattleDeckData = p0 here. Its
            // setter also writes LastBattleDeckData (see ContinuousController
            // .BattleDeckData), which RoomManager republishes as a Photon room
            // property and reuses for the user's next manually-started game.
            // Both real readers of BattleDeckData (CardObjectController.
            // CreatePlayerDecks, twice) check HarnessDeckOverrideP0/P1 FIRST
            // and short-circuit before ever reaching BattleDeckData, so this
            // assignment was dead code whose only live effect was clobbering
            // the user's saved deck with "harness-p0".

            // Auto mode is what actually plays the game: it drives the local
            // seat's mulligan, breeding, and main phase. Without this the job
            // would load a board and then sit waiting for a human.
            ContinuousController.instance.isAI = true;
            if (GManager.instance != null)
            {
                GManager.instance.isAuto = true;
            }
            // [Harness mod - C2] The assignment above is best-effort only:
            // GManager has no DontDestroyOnLoad, so on the FIRST job
            // GManager.instance is still null (skipped by the guard) and on
            // every LATER job it targets the outgoing instance that
            // SceneManager.LoadScene("BattleScene") below is about to
            // destroy. The freshly-created GManager for the loaded scene only
            // ever CLEARS isAuto in AwakeCoroutine. The authoritative
            // enable-auto-mode path is now GManager.AwakeCoroutine itself,
            // which asserts isAuto/IsAI when JobWatcher.Instance.CurrentJob is
            // non-null (see GManager.cs). This assignment is kept because it
            // is harmless and correct for any future codepath that reuses an
            // already-loaded GManager without a scene reload.

            // [Harness mod - C1] Determinism: DCGO's game-critical randomness
            // (deck shuffling via RandomUtility.ShuffledDeckCards, bot
            // decision rolls via RandomUtility.IsSucceedProbability) draws
            // from GameRandom (Xoshiro256**), NOT UnityEngine.Random.
            // GameRandom.Seed is the only seeding entry point and it is
            // otherwise seeded once from OS entropy in
            // ContinuousController.Init() -- nothing re-seeds it between here
            // and the deck shuffle, so without this call no job is
            // reproducible from its seed. GameRandom.Seed takes a `long`
            // directly, so job.seed needs no truncation.
            GameRandom.Seed(job.seed);
            // UnityEngine.Random.InitState is kept as a secondary seed for
            // the handful of non-game-critical paths (VFX, camera shake,
            // etc.) that still pull from UnityEngine.Random. It is NOT what a
            // seed-replay determinism check depends on -- GameRandom is.
            UnityEngine.Random.InitState(unchecked((int)job.seed));
            Debug.Log("[Harness] job " + job.job_id + " seeded GameRandom with seed=" + job.seed);

            Time.timeScale = HarnessConfig.TimeScale;
            return true;
        }

        private void Fail(string claimedPath, string message)
        {
            Debug.LogError("[Harness] " + message + " (" + claimedPath + ")");
            try
            {
                string dest = Path.Combine(HarnessConfig.FailedDir, Path.GetFileName(claimedPath));
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(claimedPath, dest);
            }
            catch (Exception e)
            {
                Debug.LogError("[Harness] could not file failure: " + e.Message);
            }
            CurrentJob = null;
            ClaimedPath = null;
            ClearOverrides();
        }

        /// <summary>
        /// Releases every static/global override ApplyJob applies for the
        /// duration of a job.
        /// </summary>
        /// <remarks>
        /// [Harness mod - I3] Without this, harness state outlives the job
        /// that set it: HarnessDeckOverrideP0/P1 keep redirecting
        /// CardObjectController.CreatePlayerDecks to harness decks, and
        /// Time.timeScale stays at 8 for the rest of the process, for any
        /// game the user starts by hand after the batch drains. Called both
        /// on job failure and when a poll finds the queue empty while idle.
        /// </remarks>
        private static void ClearOverrides()
        {
            CardObjectController.HarnessDeckOverrideP0 = null;
            CardObjectController.HarnessDeckOverrideP1 = null;
            Time.timeScale = 1f;

            // isAI is what makes GManager treat the session as a bot game, and
            // it lives on the DontDestroyOnLoad ContinuousController, so it
            // outlives the batch too. Leaving it set pushes the user's next
            // hand-started game into auto mode.
            if (ContinuousController.instance != null)
            {
                ContinuousController.instance.isAI = false;
            }
        }

        private static string SafeRead(string path)
        {
            try { return File.ReadAllText(path); }
            catch (Exception) { return null; }
        }
    }
}
