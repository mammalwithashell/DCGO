using System;
using System.Collections;
using System.IO;
using Photon.Pun;
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

        // [Harness mod] Turns seen by the CURRENT job, reset on every claim.
        // Enforces limits.max_turns (Step 4) so a bot that loops forever
        // (our own engine hit exactly this class of bug -- the CannotAttack
        // mask loop) hangs a single job instead of the whole batch.
        private int _turnsSeen;

        // [Harness mod - D2] Edge-trigger for ClearOverrides: true once ApplyJob
        // has installed the harness overrides (deck overrides / isAI /
        // timeScale) for the CURRENT claim attempt, false once ClearOverrides
        // has released them. See ClearOverrides for why this can't be a plain
        // "clear every idle poll" call.
        private static bool _overridesApplied;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            // Always announce, even when disabled. A harness that is off looks
            // exactly like a harness that is broken -- Play starts, nothing
            // happens, no message. One line here turns "it just sat there" into
            // a self-diagnosing state, and names the root so the Unity side and
            // the CLI side can be checked for agreement at a glance.
            Debug.Log("[Harness] bootstrap: enabled=" + HarnessConfig.Enabled
                      + " root=" + HarnessConfig.Root);

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

        /// <summary>
        /// True once DCGO has finished loading the card database a job needs to
        /// resolve its decks.
        /// </summary>
        /// <remarks>
        /// [Harness mod] Bootstrap runs at BeforeSceneLoad, roughly a second
        /// ahead of ContinuousController.Init() populating SortedCardList. A
        /// poll inside that window claims a job, fails deck resolution, and
        /// files it to failed/ -- burning real jobs on a startup race rather
        /// than on anything wrong with them. Gate claiming on readiness instead.
        /// </remarks>
        private static bool DcgoReady =>
            ContinuousController.instance != null
            && ContinuousController.instance.SortedCardList != null
            && ContinuousController.instance.SortedCardList.Length > 0;

        private IEnumerator PollLoop()
        {
            // Announce the wait once, so a slow start is distinguishable from a
            // harness that is simply not working.
            if (!DcgoReady)
            {
                Debug.Log("[Harness] waiting for DCGO to finish loading its card database...");
                yield return new WaitWhile(() => !DcgoReady);
                Debug.Log("[Harness] card database ready; claiming jobs.");
            }

            while (true)
            {
                if (CurrentJob == null && DcgoReady)
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
            // [Harness mod] Reset the turn-cap counter for the job just
            // claimed; NotifyTurnStarted counts against this job only.
            _turnsSeen = 0;
            Debug.Log("[Harness] started job " + job.job_id);

            // Same handoff the auto-mode restart uses.
            ContinuousController.instance.isAI = true;
            StartCoroutine(LoadBattleSceneWhenPhotonReady());
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
            // [Harness mod - D2] Mark overrides as installed for this claim
            // attempt (deck overrides above; isAI / Time.timeScale below) so
            // ClearOverrides knows there is something to release. Set as soon
            // as the first override is written, not at the end of ApplyJob,
            // so a later exception mid-ApplyJob (routed to Fail -> Clear
            // Overrides by the caller) still results in a real clear instead
            // of a no-op that would leave these overrides stuck on.
            _overridesApplied = true;
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
            // GameRandom.Seed takes a `long` directly, so job.seed needs no
            // truncation.
            //
            // [Harness mod - D1] This is belt-and-braces only, NOT the
            // authoritative seed: TurnStateMachine.Init re-seeds GameRandom
            // from OS entropy on every game via the "乱数列初期化" RPC
            // handshake (SetRandom -> SetRandomCoroutine -> GameRandom.Seed),
            // which runs after LoadScene("BattleScene") and immediately
            // before the deck shuffle -- overwriting whatever is set here.
            // That handshake now sources its seed from
            // JobWatcher.Instance.CurrentJob.seed when a job is active (see
            // TurnStateMachine.cs), which is what actually makes a job
            // reproducible. This call is kept in case some future codepath
            // reads GameRandom before the handshake fires.
            GameRandom.Seed(job.seed);
            // UnityEngine.Random.InitState is kept as a secondary seed for
            // the handful of non-game-critical paths (VFX, camera shake,
            // etc.) that still pull from UnityEngine.Random. It is NOT what a
            // seed-replay determinism check depends on -- GameRandom is.
            UnityEngine.Random.InitState(unchecked((int)job.seed));
            // [Harness mod - D1] "early-seeded": see the belt-and-braces note
            // above -- the TurnStateMachine handshake log is the one that
            // confirms the seed actually used for the shuffle.
            Debug.Log("[Harness] job " + job.job_id + " early-seeded GameRandom with seed=" + job.seed);

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
            // [Harness mod] Now shares the release step with the success path
            // (JobResultWriter.FileResult) instead of duplicating the two
            // field clears inline.
            ClearCurrentJob();
            ClearOverrides();
        }

        /// <summary>Release the current job so the poll loop claims the next one.</summary>
        // [Harness mod] The success-path counterpart to Fail's job release.
        // Called from JobResultWriter.FileResult after a completed/partial
        // result is filed. Before this method existed CurrentJob was cleared
        // ONLY inside Fail; PollLoop only calls TryClaimAndStart when
        // CurrentJob == null, so a job that actually finished (rather than
        // failing) left CurrentJob set forever and the harness stalled after
        // exactly one game.
        public void ClearCurrentJob()
        {
            CurrentJob = null;
            ClaimedPath = null;
        }

        /// <summary>
        /// Called at the start of each turn. Abandons the job past its turn cap,
        /// filing a "partial" result -- the truncated recording is still a valid
        /// parity input, and an abandoned game beats a hung batch.
        /// </summary>
        public void NotifyTurnStarted()
        {
            if (CurrentJob == null) return;
            _turnsSeen++;
            if (_turnsSeen <= CurrentJob.limits.max_turns) return;

            Debug.LogWarning("[Harness] job " + CurrentJob.job_id + " hit the turn cap; abandoning");
            JobResultWriter.FileResult("partial", _turnsSeen, "exceeded max_turns");
            _turnsSeen = 0;
            // Reloading kills the running game; the poll loop claims the next job.
            SceneManager.LoadScene("BattleScene");
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
        ///
        /// [Harness mod - D2] The idle-poll call site (TryClaimAndStart, once
        /// per PollSeconds -- 1s -- whenever the job queue is empty, which is
        /// the normal state between batches and effectively all the time
        /// otherwise) used to run this unconditionally on every single poll.
        /// SelectBattleMode.StartSelectBattleDeck sets isAI = true for a
        /// human-started vs-AI game and then spends several seconds in deck
        /// selection / a loading coroutine before BattleScene loads; an idle
        /// poll landing in that window silently flipped isAI back to false
        /// and stomped Time.timeScale, hanging the human's game. Guarding on
        /// _overridesApplied makes this edge-triggered: it now does real work
        /// exactly once per job (success or failure), immediately after
        /// ApplyJob installed the overrides, and is a no-op every other poll
        /// -- including every idle poll where no harness job is in flight, so
        /// it can no longer race a human-started game.
        /// </remarks>
        private static void ClearOverrides()
        {
            if (!_overridesApplied) return;
            _overridesApplied = false;

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

        /// <summary>
        /// Leave any Photon room left over from the previous job, then load the
        /// battle scene.
        /// </summary>
        /// <remarks>
        /// [Harness mod] DCGO's AI mode is not offline: TurnStateMachine.Init
        /// connects to Photon and creates a private one-seat room. DCGO normally
        /// leaves that room on the exit-to-menu path, which the harness bypasses
        /// so it can chain jobs. The room therefore survived into the next job,
        /// where Init does:
        ///     if (!InLobby) JoinLobby();
        ///     yield return new WaitWhile(() =&gt; !InLobby);
        /// Photon refuses to join a lobby while still in a room, so that wait
        /// never completed -- the second game of every batch hung on "Now
        /// Loading" with no error. Leave the room and wait for it to take effect
        /// before loading, so Init starts from the same clean state the first
        /// job enjoyed.
        /// </remarks>
        private IEnumerator LoadBattleSceneWhenPhotonReady()
        {
            if (PhotonNetwork.InRoom)
            {
                Debug.Log("[Harness] leaving the previous job's Photon room");
                PhotonNetwork.LeaveRoom();

                float deadline = Time.realtimeSinceStartup + 15f;
                yield return new WaitWhile(() =>
                    PhotonNetwork.InRoom && Time.realtimeSinceStartup < deadline);

                if (PhotonNetwork.InRoom)
                {
                    // Do not load into a state we know hangs; fail loudly so the
                    // job is requeued rather than silently stalling the batch.
                    Fail(ClaimedPath, "timed out leaving the previous Photon room");
                    yield break;
                }
            }

            SceneManager.LoadScene("BattleScene");
        }

        private static string SafeRead(string path)
        {
            try { return File.ReadAllText(path); }
            catch (Exception) { return null; }
        }
    }
}
