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
                    if (job.inputs[i].select_card_ids == null)
                    {
                        job.inputs[i].select_card_ids = new string[0];
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

        // -- Selection payload (exam `select:` steps) ---------------------
        // A selection step answers one of the ~10 [PunRPC] selection prompts
        // instead of carrying a 2192-space action id. The wire carries card
        // IDENTITIES, never engine-internal indices; each hook resolves them
        // against ITS OWN candidate list via SelectionAnswer.MatchCardIds.
        // Absent = not a selection step (see IsSelection).

        /// <summary>
        /// Identity picks, in pick order. For permanent prompts these are the
        /// targeted permanents' TOP-CARD ids. Duplicates resolve in occurrence
        /// order against the prompt's candidate list (documented limitation).
        /// </summary>
        public string[] select_card_ids = new string[0];

        /// <summary>
        /// Count VALUE / generic int / attack-target encoding (-1 = attack
        /// the player). Sentinel <see cref="int.MinValue"/> means absent:
        /// JsonUtility has no absent-key attribute for ints, but it only
        /// overwrites a field initializer when the key is present in the JSON,
        /// so the initializer IS the absent default.
        /// </summary>
        public int select_value = int.MinValue;

        /// <summary>
        /// Which of the SAME identity's candidates to take, 0-based, when
        /// <see cref="select_card_ids"/> names a card the prompt offers more
        /// than once. Same <see cref="int.MinValue"/> absent sentinel as
        /// <see cref="select_value"/>.
        /// </summary>
        /// <remarks>
        /// Introduced for the MultipleSkills (trigger-order) prompt, whose
        /// candidates are stacked TRIGGERS rather than interchangeable copies:
        /// a deleted carrier with an [On Deletion] and an &lt;Ascension&gt;
        /// offers its own identity twice, and those are different decisions, so
        /// occurrence order must not silently pick between them (see
        /// <see cref="SelectionAnswer.MatchOneWithOrdinal"/>).
        ///
        /// It is deliberately NOT <see cref="select_value"/> reused: that field
        /// stays the raw DCGO-index fallback, and one field meaning "an index
        /// into DCGO's list" in one step and "an index within one card's own
        /// triggers" in the next is exactly the value-space confusion this
        /// whole payload exists to end.
        /// </remarks>
        public int select_ordinal = int.MinValue;

        /// <summary>
        /// Which of ONE card's simultaneous triggers to resolve, named by its
        /// KEYWORD rather than by position -- the semantic sibling of
        /// <see cref="select_ordinal"/>, and the preferred one.
        /// </summary>
        /// <remarks>
        /// <see cref="select_ordinal"/> is a 0-based position in the PROMPTING
        /// ENGINE'S OWN candidate list, and the two engines do not build that
        /// list in the same order. EX12-047 Amaterasumon is the worked example:
        /// DCGO registers Ascension (EX12_047.cs:41) BEFORE the printed
        /// [On Deletion] (:182), while the Rust engine enumerates them the other
        /// way round -- so one authored ordinal silently means a DIFFERENT
        /// trigger on each side, and both then diverge several rows later
        /// against a board the wrong effect has already mutated.
        ///
        /// The value arrives NORMALIZED (lowercased, with angle brackets and
        /// whitespace stripped) so that the printed "Armor Purge" keyword and
        /// DCGO's own effect name both land on "armorpurge". Normalize this side
        /// identically -- see SelectionAnswer.NormalizeTriggerName.
        ///
        /// Mutually exclusive with <see cref="select_ordinal"/>; a step carrying
        /// both is refused rather than silently preferring one.
        /// </remarks>
        public string select_trigger = null;

        /// <summary>
        /// The branch to EXCLUDE -- the complement of
        /// <see cref="select_trigger"/>, for a wanted branch that carries no
        /// keyword of its own.
        /// </summary>
        /// <remarks>
        /// EX12-047 Amaterasumon is the case: its deletion stack is
        /// [Ascension, the printed On Deletion], and only the first can be
        /// named by keyword. Nothing else separates them -- same source card,
        /// same timing (both register under OnDestroyedAnyone), same
        /// optionality.
        ///
        /// Neither engine can answer "which branch is NOT a keyword" without a
        /// registry of what counts as one, and this repo has none: no
        /// IsKeywordEffect flag, no keyword enum, and Decode's effect name is
        /// parameterized, so a hardcoded name list would be brittle. Both sides
        /// CAN drop a NAMED branch and require exactly one survivor, which is
        /// what this field asks for.
        ///
        /// Arrives normalized, same as select_trigger. Mutually exclusive with
        /// both select_trigger and select_ordinal.
        /// </remarks>
        public string select_trigger_not = null;

        /// <summary>True when <see cref="select_bool"/> carries an answer
        /// (a bare bool cannot distinguish "false" from "absent").</summary>
        public bool select_has_bool;

        /// <summary>OptionalSkill / generic_bool answer; only meaningful when
        /// <see cref="select_has_bool"/> is true.</summary>
        public bool select_bool;

        /// <summary>Decline / cancel the prompt outright.</summary>
        public bool select_cancel;

        /// <summary>
        /// True when this step carries a selection payload -- i.e. it answers
        /// a selection prompt rather than naming a main-phase/breeding action.
        /// A selection step arriving at an action-id prompt (or vice versa)
        /// is a prompt mismatch and aborts the job as a finding.
        /// </summary>
        public bool IsSelection =>
            (select_card_ids != null && select_card_ids.Length > 0)
            || select_value != int.MinValue
            || select_ordinal != int.MinValue
            || !string.IsNullOrEmpty(select_trigger)
            || !string.IsNullOrEmpty(select_trigger_not)
            || select_has_bool
            || select_cancel;
    }

    [Serializable]
    public class HarnessJobLimits
    {
        public int max_turns = 40;
        public int timeout_seconds = 180;
    }
}
