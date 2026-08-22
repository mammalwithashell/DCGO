using System;
using System.Collections.Generic;

namespace Digimon.Harness
{
    /// <summary>What DCGO is asking, at the moment it asks.</summary>
    public class PromptContext
    {
        /// <summary>
        /// Prompt kind, matching the recorder's `selection.prompt` vocabulary.
        /// </summary>
        /// <remarks>
        /// The vocabulary is CLOSED and is exactly these 13 kinds:
        ///
        ///   10 selection prompts, each named for the class whose [PunRPC]
        ///   the driver intercepts alongside `GameRecorder.LogSelectionRow`:
        ///     SelectCardEffect       (SetTargetCardAndIndicies)
        ///     SelectHandEffect       (SetTargetHandCards)
        ///     SelectPermanentEffect  (SetTargetFrames)
        ///     SelectAttackEffect     (SetAttackTarget)
        ///     SelectCountEffect      (SetCount)
        ///     SelectDigiXrosClass    (SetTargetDigiXrossIndex)
        ///     MultipleSkills         (SetTargetSkill)
        ///     OptionalSkill          (SetUseOptional)
        ///     generic_int            (UserSelectionManager.SetIntForPlayer)
        ///     generic_bool           (UserSelectionManager.SetBoolForPlayer)
        ///
        ///   plus 3 decision families that are NOT LogSelectionRow rows, but
        ///   which a line must cover or it cannot get past turn 1:
        ///     mulligan               (TurnStateMachine, LogMulligan)
        ///     breeding_action        (TurnStateMachine, LogBreedingAction)
        ///     main_phase             (TurnStateMachine.QueueMainPhaseAction)
        ///
        /// `generic_int` / `generic_bool` are the only two kinds that are not a
        /// class name; they are the fallback channel, and their rows carry no
        /// candidate list.
        ///
        /// Keeping this identical to what the recorder writes is load-bearing:
        /// the driver and the recorder hook the same call sites, so a scenario
        /// authored from a recording can always be replayed by construction.
        /// </remarks>
        public string Kind;
        /// <summary>Number of picks required, or -1 when the site does not know.</summary>
        public int Count = -1;
        /// <summary>Selectable card IDs, unordered.</summary>
        public string[] Candidates = new string[0];
    }

    /// <summary>
    /// The scripted action cursor, plus the assertion that DCGO is asking the
    /// question the author expected.
    /// </summary>
    /// <remarks>
    /// Pure by design: no MonoBehaviour, no Unity types, no statics. That is
    /// what makes the whole decision surface unit-testable, since DCGO has no
    /// way to test a MonoBehaviour mid-game.
    ///
    /// The assertion is the point of the class. A driver that answers whatever
    /// it is asked will, on a single ordering mismatch, desynchronize the
    /// entire remainder of the line while every step still looks successful --
    /// and report a confident wrong answer.
    /// </remarks>
    public class ScriptedLine
    {
        private readonly HarnessJobStep[] _steps;
        private int _cursor;

        public ScriptedLine(HarnessJobStep[] steps)
        {
            _steps = steps ?? new HarnessJobStep[0];
        }

        public int Cursor => _cursor;
        public bool IsExhausted => _cursor >= _steps.Length;

        /// <summary>
        /// Take the next scripted action id if it matches what is being asked.
        /// Advances the cursor only on success.
        /// </summary>
        /// <remarks>
        /// Thin wrapper over <see cref="TryTakeStep"/> for the action-id
        /// prompt kinds (main_phase / breeding_action / mulligan). The guard
        /// that a SELECTION step must not answer an action-id prompt lives in
        /// <c>InputDriver.TryAnswer</c>, which sees the whole step.
        /// </remarks>
        public bool TryTake(int actor, PromptContext ctx, out int actionId, out string mismatch)
        {
            actionId = -1;
            HarnessJobStep step;
            if (!TryTakeStep(actor, ctx, out step, out mismatch)) return false;
            actionId = step.action_id;
            return true;
        }

        /// <summary>
        /// Take the next scripted step -- the WHOLE step, selection payload
        /// included -- if it matches what is being asked. Advances the cursor
        /// only on success.
        /// </summary>
        public bool TryTakeStep(int actor, PromptContext ctx, out HarnessJobStep step, out string mismatch)
        {
            step = null;
            mismatch = null;

            if (IsExhausted)
            {
                // NOT a silent fall-through to AutoSelect. DCGO asking a
                // question the line has no answer for means the two engines
                // disagree about how many decisions this position contains --
                // which is a finding, not an error.
                mismatch = "line exhausted after " + _steps.Length +
                           " steps, but DCGO asked actor " + actor +
                           " a '" + (ctx == null ? "<null>" : ctx.Kind) + "' prompt";
                return false;
            }

            HarnessJobStep candidate = _steps[_cursor];

            if (candidate.actor != actor)
            {
                mismatch = "step " + _cursor + " expected actor " + candidate.actor +
                           " but DCGO asked actor " + actor;
                return false;
            }

            string kind = ctx == null ? null : ctx.Kind;

            if (!string.IsNullOrEmpty(candidate.expect_prompt) && candidate.expect_prompt != kind)
            {
                mismatch = "step " + _cursor + " expected prompt '" + candidate.expect_prompt +
                           "' but DCGO asked '" + kind + "'";
                return false;
            }

            if (candidate.expect_count >= 0 && ctx != null && ctx.Count >= 0 &&
                candidate.expect_count != ctx.Count)
            {
                mismatch = "step " + _cursor + " expected count " + candidate.expect_count +
                           " but DCGO asked for count " + ctx.Count;
                return false;
            }

            if (candidate.expect_candidates != null && candidate.expect_candidates.Length > 0)
            {
                string[] actual = (ctx == null || ctx.Candidates == null)
                    ? new string[0] : ctx.Candidates;
                if (!SameMultiset(candidate.expect_candidates, actual))
                {
                    mismatch = "step " + _cursor + " expected candidates [" +
                               string.Join(",", candidate.expect_candidates) +
                               "] but DCGO offered [" + string.Join(",", actual) + "]";
                    return false;
                }
            }

            step = candidate;
            _cursor++;
            return true;
        }

        /// <summary>
        /// Order-insensitive, duplicate-sensitive comparison. Candidate order is
        /// a DCGO presentation detail, but the NUMBER of copies offered is
        /// semantics -- so this is a multiset compare, not a set compare.
        /// </summary>
        private static bool SameMultiset(string[] a, string[] b)
        {
            if (a.Length != b.Length) return false;
            Dictionary<string, int> counts = new Dictionary<string, int>();
            foreach (string s in a)
            {
                counts.TryGetValue(s ?? "", out int n);
                counts[s ?? ""] = n + 1;
            }
            foreach (string s in b)
            {
                string k = s ?? "";
                if (!counts.TryGetValue(k, out int n) || n == 0) return false;
                counts[k] = n - 1;
            }
            foreach (KeyValuePair<string, int> kv in counts)
            {
                if (kv.Value != 0) return false;
            }
            return true;
        }
    }
}
