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
            if (files.Length == 0) return;

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

            HarnessJob job = HarnessJob.Parse(SafeRead(claimed));
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
            ContinuousController.instance.BattleDeckData = p0;

            // Auto mode is what actually plays the game: it drives the local
            // seat's mulligan, breeding, and main phase. Without this the job
            // would load a board and then sit waiting for a human.
            ContinuousController.instance.isAI = true;
            if (GManager.instance != null)
            {
                GManager.instance.isAuto = true;
            }

            // Determinism: every random draw in this game derives from the
            // job's seed, so a divergence found in game 137 can be re-run.
            UnityEngine.Random.InitState(unchecked((int)job.seed));

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
        }

        private static string SafeRead(string path)
        {
            try { return File.ReadAllText(path); }
            catch (Exception) { return null; }
        }
    }
}
