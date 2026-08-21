using System;
using UnityEngine;

namespace Digimon.Harness
{
    /// <summary>
    /// One unattended game, as written by the `dcgo-harness submit` CLI.
    /// </summary>
    /// <remarks>
    /// Parsed with Unity's <see cref="JsonUtility"/>, which handles nested
    /// [Serializable] classes and arrays and silently ignores unknown fields —
    /// exactly the forward-compatibility we want.
    ///
    /// Field names are snake_case to match the JSON verbatim; JsonUtility has no
    /// name-mapping attribute, so the C# fields wear the wire names.
    /// </remarks>
    [Serializable]
    public class HarnessJob
    {
        public string job_id;
        public string policy;
        public HarnessJobDecks decks;
        public int first_player;
        public long seed;
        public HarnessJobLimits limits;

        /// <summary>Fixed prefix of the initial draw order, per seat. May be empty.</summary>
        public HarnessJobDeckOrder deck_order;

        /// <summary>The scripted line. Empty for `policy: "ai"` jobs.</summary>
        public HarnessJobStep[] inputs;

        /// <summary>
        /// True when this job carries a line for <see cref="InputDriver"/> to
        /// play. Keyed off the policy string rather than off `inputs.Length`
        /// so a malformed scripted job fails loudly instead of silently
        /// degrading into an AI game that reports a plausible-looking result.
        /// </summary>
        public bool IsScripted => policy == "scripted";

        /// <summary>Parse a job file. Returns null when the text is unusable.</summary>
        public static HarnessJob Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                HarnessJob job = JsonUtility.FromJson<HarnessJob>(json);
                if (job == null || string.IsNullOrEmpty(job.job_id)) return null;
                if (job.decks == null || job.decks.p0 == null || job.decks.p1 == null) return null;
                if (job.limits == null) job.limits = new HarnessJobLimits();

                // Normalize absent optional collections to empty rather than
                // null. Every consumer would otherwise need its own null guard,
                // and one missing guard is a NullReferenceException mid-game.
                if (job.deck_order == null) job.deck_order = new HarnessJobDeckOrder();
                if (job.deck_order.p0 == null) job.deck_order.p0 = new string[0];
                if (job.deck_order.p1 == null) job.deck_order.p1 = new string[0];
                if (job.inputs == null) job.inputs = new HarnessJobStep[0];
                for (int i = 0; i < job.inputs.Length; i++)
                {
                    if (job.inputs[i] == null) job.inputs[i] = new HarnessJobStep();
                    if (job.inputs[i].expect_candidates == null)
                    {
                        job.inputs[i].expect_candidates = new string[0];
                    }
                }

                // A scripted job with no line would start a game nobody drives
                // and hang until the timeout -- indistinguishable from a hung
                // Unity, which is the failure mode the heartbeat exists to make
                // legible. Reject it here instead.
                if (job.IsScripted && job.inputs.Length == 0)
                {
                    Debug.LogError("[Harness] scripted job " + job.job_id + " carries no inputs");
                    return null;
                }

                return job;
            }
            catch (Exception e)
            {
                Debug.LogError("[Harness] job parse failed: " + e.Message);
                return null;
            }
        }
    }

    [Serializable]
    public class HarnessJobDecks
    {
        public string[] p0;
        public string[] p1;
    }

    [Serializable]
    public class HarnessJobDeckOrder
    {
        public string[] p0 = new string[0];
        public string[] p1 = new string[0];
    }

    /// <summary>
    /// One scripted decision: the action to feed, plus the prompt the author
    /// expects DCGO to be asking at that moment.
    /// </summary>
    /// <remarks>
    /// `expect_prompt` is asserted BEFORE the action is fed. A driver that
    /// answers whatever it is asked will, on a single ordering mismatch,
    /// desynchronize the entire remainder of the line while every step still
    /// looks successful.
    ///
    /// See <see cref="PromptContext.Kind"/> for the closed 13-kind prompt
    /// vocabulary `expect_prompt` is drawn from.
    /// </remarks>
    [Serializable]
    public class HarnessJobStep
    {
        public int actor;
        public int action_id;
        /// <summary>Expected prompt kind. Empty means "do not assert".</summary>
        public string expect_prompt;
        /// <summary>Expected number of picks. -1 (default) means "do not assert".</summary>
        public int expect_count = -1;
        /// <summary>Expected candidate card IDs, order-insensitive. Empty means "do not assert".</summary>
        public string[] expect_candidates = new string[0];
    }

    [Serializable]
    public class HarnessJobLimits
    {
        public int max_turns = 40;
        public int timeout_seconds = 180;
    }
}
