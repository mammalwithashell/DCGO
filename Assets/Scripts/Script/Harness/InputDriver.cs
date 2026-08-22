using System.Collections.Generic;
using UnityEngine;

namespace Digimon.Harness
{
    /// <summary>
    /// Feeds a scripted line into DCGO at the decision points that would
    /// otherwise be answered by <c>AutoSelect()</c> / the bot.
    /// </summary>
    /// <remarks>
    /// INTERCEPTION SEAM -- read this before adding a site.
    ///
    /// The driver is consulted at the ~10 <c>[PunRPC]</c> selection entry
    /// points where <c>GameRecorder.LogSelectionRow</c> already hooks, plus the
    /// three decision families that are not <c>LogSelectionRow</c> rows
    /// (mulligan, breeding, main phase). It is deliberately NOT consulted at
    /// the 13 <see cref="HarnessAuto.DrivesLocalSeat"/> gates, even though an
    /// earlier plan said so:
    ///
    ///  1. The driver and the recorder hook the SAME call site, so the prompt
    ///     vocabulary is one vocabulary by construction.
    ///  2. The RPC seam is uniform -- one shape, one place -- where the gates
    ///     are 13 heterogeneous sites with two polarities and two control-flow
    ///     shapes (<c>yield break</c> vs <c>return</c>).
    ///  3. It dissolves the <c>CardController.cs:700</c> problem: that gate has
    ///     no <c>AutoSelect</c> of its own, it only sets flags and falls
    ///     through to <c>selectCountEffect.Activate()</c>, whose answer
    ///     surfaces downstream as the <c>SelectCountEffect</c> prompt. At the
    ///     RPC layer it needs no hook at all.
    ///  4. The AI still computes a throwaway value first. That is pure
    ///     selection logic with no side effects, so discarding it is safe.
    ///
    /// Every site follows the same shape: build the prompt context, call
    /// <see cref="TryAnswerStep"/> (selection prompts) or
    /// <see cref="TryAnswer(int, string, int, IList{string}, out int)"/>
    /// (action-id prompts) BEFORE the existing <c>LogSelectionRow</c> call,
    /// and on <c>true</c> substitute the scripted answer for the AI's before
    /// anything else in the method runs -- so what the recorder writes is what
    /// the script asked for, not what the AI happened to pick.
    ///
    /// A <c>false</c> return is NEVER "the script declined". Either no scripted
    /// job is running (<see cref="IsActive"/> was false), or the line
    /// mismatched -- and a mismatch has already aborted the job. Call sites
    /// must not fall through to a second interpretation of a false return.
    ///
    /// Deliberately thin. Every decision this class could get wrong lives in
    /// <see cref="ScriptedLine"/>, which is unit-tested; DCGO has no way to
    /// test a MonoBehaviour mid-game, so anything untestable is kept small
    /// enough to review by eye.
    /// </remarks>
    public static class InputDriver
    {
        private static ScriptedLine _line;

        // -- Prompt kinds -------------------------------------------------
        // The CLOSED 13-kind vocabulary. These strings are the same literals
        // the recorder passes to LogSelectionRow at the same call sites; they
        // are named here only so a typo at a call site is a compile error
        // rather than a silent, permanent prompt mismatch.

        public const string KindSelectCard      = "SelectCardEffect";
        public const string KindSelectHand      = "SelectHandEffect";
        public const string KindSelectPermanent = "SelectPermanentEffect";
        public const string KindSelectAttack    = "SelectAttackEffect";
        public const string KindSelectCount     = "SelectCountEffect";
        public const string KindSelectDigiXros  = "SelectDigiXrosClass";
        public const string KindMultipleSkills  = "MultipleSkills";
        public const string KindOptionalSkill   = "OptionalSkill";
        public const string KindGenericInt      = "generic_int";
        public const string KindGenericBool     = "generic_bool";
        public const string KindMulligan        = "mulligan";
        public const string KindBreedingAction  = "breeding_action";
        public const string KindMainPhase       = "main_phase";

        // (The Cancel/Decline int sentinels of the retired single-int
        // selection encoding were removed: cancel/decline now travel as the
        // step's `select_cancel` field, and "attack the player" as
        // `select_value: -1`.)

        /// <summary>True while a scripted job is driving both seats.</summary>
        public static bool IsActive
        {
            get { return _line != null; }
        }

        public static void Install(HarnessJob job)
        {
            if (job == null || !job.IsScripted) { _line = null; return; }
            _line = new ScriptedLine(job.inputs);
            Debug.Log("[Harness] scripted line installed: " + job.inputs.Length + " steps");
        }

        public static void Release()
        {
            _line = null;
        }

        /// <summary>
        /// True when the line ran to its end. Checked at game end so a job whose
        /// line stopped early is not filed as a clean completion.
        /// </summary>
        public static bool IsExhausted
        {
            get { return _line == null || _line.IsExhausted; }
        }

        /// <summary>How many scripted steps have been consumed.</summary>
        public static int Cursor
        {
            get { return _line == null ? 0 : _line.Cursor; }
        }

        /// <summary>
        /// Supply the next scripted answer for <paramref name="actor"/>, if this
        /// is the question the line expects. Aborts the job on a mismatch.
        /// </summary>
        /// <remarks>
        /// ANSWER ENCODING. This int overload serves ONLY the three action-id
        /// kinds; <c>HarnessJobStep.action_id</c> is interpreted per kind:
        ///
        ///   mulligan               0 = keep, 1 = redraw (matches
        ///                          <c>ActionEncoder.EncodeMulligan</c>).
        ///   breeding_action        the engine action id the recorder logs:
        ///                          62 (PASS) = decline, 60 (HATCH) /
        ///                          61 (MOVE_FROM_BREEDING) = do it.
        ///   main_phase             a 2192-space action id, decoded by
        ///                          <see cref="BuildMainPhaseAction"/>.
        ///
        /// The ~10 SELECTION prompts do NOT answer through this overload any
        /// more: their hooks call <see cref="TryAnswerStep"/> and consume the
        /// step's `select_*` payload (card identities matched via
        /// <see cref="SelectionAnswer.MatchCardIds"/>, count/int values,
        /// bools, cancel) -- which is what makes multi-pick answers
        /// expressible at all. A step carrying a selection payload that
        /// arrives HERE is a prompt mismatch and aborts as a finding.
        /// </remarks>
        public static bool TryAnswer(int actor, PromptContext ctx, out int actionId)
        {
            actionId = -1;

            HarnessJobStep step;
            if (!ResolveStep(actor, ctx, out step)) return false;

            // A selection payload arriving at an action-id prompt means the
            // author scripted a selection answer for a decision DCGO models as
            // a main-phase/breeding/mulligan action -- the same "the two
            // engines disagree about what this decision IS" finding as a kind
            // mismatch, and it aborts the same way.
            if (step.IsSelection)
            {
                Abort("prompt mismatch: step " + (Cursor - 1) + " carries a selection payload (" +
                      SelectionAnswer.Describe(step) + ") but DCGO asked a '" +
                      (ctx == null ? "<null>" : ctx.Kind) + "' prompt, which takes an action id");
                return false;
            }

            actionId = step.action_id;
            return true;
        }

        /// <summary>
        /// Supply the next scripted STEP for <paramref name="actor"/> -- the
        /// whole step, selection payload included -- if this is the question
        /// the line expects. Same cursor/assert semantics as
        /// <see cref="TryAnswer(int, PromptContext, out int)"/>: the prompt is
        /// asserted BEFORE it is answered, a mismatch aborts the job as a
        /// finding, and exhaustion completes the line. The selection RPC hooks
        /// use this and map the step's `select_*` fields onto their own RPC
        /// payloads (see SelectionAnswer.MatchCardIds for identity picks).
        /// </summary>
        public static bool TryAnswerStep(int actor, string kind, int count,
                                         IList<string> candidates, out HarnessJobStep step)
        {
            PromptContext ctx = new PromptContext
            {
                Kind = kind,
                Count = count,
                Candidates = candidates == null ? new string[0] : ToArray(candidates),
            };
            return ResolveStep(actor, ctx, out step);
        }

        /// <summary>
        /// The one shared consume path: take the next step (asserting the
        /// prompt first), complete the line on exhaustion, abort on mismatch.
        /// </summary>
        private static bool ResolveStep(int actor, PromptContext ctx, out HarnessJobStep step)
        {
            step = null;
            if (_line == null) return false;

            string mismatch;
            if (_line.TryTakeStep(actor, ctx, out step, out mismatch)) return true;

            // Running off the END of the line is NORMAL termination, not a
            // desync. A scenario is a probe: it drives the position it cares
            // about and stops, and DCGO would keep asking for the rest of the
            // match regardless. Ending here keeps the recording and the state
            // sidecar, which are exactly what the differ needs.
            //
            // The genuine finding -- "our engine expected a choice here and
            // DCGO never asked", or asked a different one -- surfaces as a
            // mismatch on a SPECIFIC step below, not as exhaustion. Conflating
            // the two made every finished scenario read as a failure.
            if (_line.IsExhausted)
            {
                _line = null;
                if (JobWatcher.Instance != null)
                {
                    JobWatcher.Instance.CompleteScriptedLine();
                }
                return false;
            }

            // A prompt mismatch is a FINDING, not an error: "our engine expected
            // a choice here and DCGO never asked" (or asked a different one) is
            // exactly the divergence class that never surfaces as an illegal
            // action. Abort loudly rather than answering blind -- answering
            // would desync the whole remainder while every later step still
            // looked successful.
            Abort("prompt mismatch: " + mismatch);
            return false;
        }

        /// <summary>
        /// Convenience overload so a call site stays three lines, not eight.
        /// </summary>
        /// <param name="count">Picks the prompt is asking for, or -1 when the
        /// site does not know.</param>
        /// <param name="candidates">Card IDs offered, or null when the site
        /// cannot name them cheaply. An absent candidate list means NOT
        /// MEASURED: a scripted step that asserts candidates against a site
        /// which cannot report them then fails loudly rather than passing on
        /// an assertion nobody actually checked.</param>
        public static bool TryAnswer(int actor, string kind, int count,
                                     IList<string> candidates, out int actionId)
        {
            PromptContext ctx = new PromptContext
            {
                Kind = kind,
                Count = count,
                Candidates = candidates == null ? new string[0] : ToArray(candidates),
            };
            return TryAnswer(actor, ctx, out actionId);
        }

        /// <summary>
        /// Abandon the running job and file it as failed, with the reason.
        /// </summary>
        public static void Abort(string reason)
        {
            if (JobWatcher.Instance != null)
            {
                JobWatcher.Instance.AbortCurrentJob(reason);
            }
            else
            {
                Debug.LogError("[Harness] " + reason + " (no JobWatcher to abort)");
            }
            _line = null;
        }

        // -- Answer decoding helpers --------------------------------------
        // (TryDecodePermanentTarget, the side*100+index splitter for the
        // retired single-int permanent encoding, was removed when the
        // permanent/attack hooks moved to identity-matched select_card_ids.)

        /// <summary>
        /// Build the <see cref="MainPhaseAction"/> a 2192-space action id names,
        /// against <paramref name="actor"/>'s live hand and field.
        /// </summary>
        /// <remarks>
        /// A partial inverse of
        /// <c>Digimon.Recording.ActionEncoder.EncodeMainPhaseAction</c>. It
        /// covers the six action families DCGO's own AI ever queues:
        /// play-from-hand, digivolve, attack, activate-hand-effect,
        /// activate-field-effect, and pass. Anything else -- DNA digivolve,
        /// source selection, breeding-source selection, trash effects -- has no
        /// single <c>MainPhaseAction</c> shape, so it returns null with an
        /// error and the job aborts rather than quietly playing something else.
        ///
        /// Index spaces, all mirrored from <c>ActionEncoder</c>:
        ///   hand slot   -> <c>actor.HandCards[slot].CardIndex</c> (an
        ///                  ActiveCardList index, which is what
        ///                  <c>PlayCardAction</c> / <c>ActivateCardAction</c>
        ///                  carry).
        ///   field slot  -> COMPACT battle-area position for attack and
        ///                  activate-permanent; converted to a SPARSE
        ///                  <c>FieldCardFrame.FrameID</c> for a digivolve
        ///                  target, because <c>PlayCardAction.TargetFrameID</c>
        ///                  is a frame id, not a compact index. Getting that
        ///                  shift wrong mis-targets every digivolve whenever a
        ///                  lower frame is empty -- the exact bug
        ///                  <c>ActionEncoder.FrameIdToFieldSlot</c> exists to
        ///                  fix in the other direction.
        /// </remarks>
        public static MainPhaseAction BuildMainPhaseAction(int actionId, Player actor, out string error)
        {
            error = null;
            if (actor == null) { error = "no actor"; return null; }

            if (actionId == Digimon.Recording.ActionSpace.PASS)
            {
                return new PassAction();
            }

            // Play a card from hand (base play; the frame is DCGO's own
            // preferred frame, matching TurnStateMachine's AI play path).
            if (actionId >= Digimon.Recording.ActionSpace.PLAY_HAND_START
                && actionId < Digimon.Recording.ActionSpace.PLAY_HAND_END)
            {
                int handSlot = actionId - Digimon.Recording.ActionSpace.PLAY_HAND_START;
                CardSource card = HandCardAt(actor, handSlot, ref error);
                if (card == null) return null;
                return new PlayCardAction(card.CardIndex, card.PreferredFrame().FrameID,
                                          new int[0], -1, new int[0]);
            }

            // Activate a [Main] effect on a card in hand.
            if (actionId >= Digimon.Recording.ActionSpace.HAND_EFFECT_START
                && actionId < Digimon.Recording.ActionSpace.HAND_EFFECT_END)
            {
                int handSlot = actionId - Digimon.Recording.ActionSpace.HAND_EFFECT_START;
                CardSource card = HandCardAt(actor, handSlot, ref error);
                if (card == null) return null;
                return new ActivateCardAction(card.CardIndex, 0);
            }

            // Attack.
            if (actionId >= Digimon.Recording.ActionSpace.ATTACK_START
                && actionId < Digimon.Recording.ActionSpace.ATTACK_END)
            {
                int rel = actionId - Digimon.Recording.ActionSpace.ATTACK_START;
                int attacker = rel / Digimon.Recording.ActionSpace.TARGETS_PER_ATTACKER;
                int target   = rel % Digimon.Recording.ActionSpace.TARGETS_PER_ATTACKER;
                if (attacker >= actor.GetFieldPermanents().Count)
                {
                    error = "attack attacker slot " + attacker + " is empty";
                    return null;
                }
                // SECURITY_TARGET (14) is DCGO's -1 "attack the player".
                int dcgoTarget = target == Digimon.Recording.ActionSpace.SECURITY_TARGET ? -1 : target;
                if (dcgoTarget >= 0
                    && (actor.Enemy == null || dcgoTarget >= actor.Enemy.GetFieldPermanents().Count))
                {
                    error = "attack target slot " + dcgoTarget + " is empty";
                    return null;
                }
                return new AttackPermanentAction(attacker, dcgoTarget);
            }

            // Digivolve: play a hand card onto an occupied frame.
            if (actionId >= Digimon.Recording.ActionSpace.DIGIVOLVE_START
                && actionId < Digimon.Recording.ActionSpace.DIGIVOLVE_END)
            {
                int rel = actionId - Digimon.Recording.ActionSpace.DIGIVOLVE_START;
                int handSlot = rel / 15;
                int fieldSlot = rel % 15;
                CardSource card = HandCardAt(actor, handSlot, ref error);
                if (card == null) return null;

                int frameId;
                if (fieldSlot == Digimon.Recording.ActionSpace.BREEDING_TARGET)
                {
                    frameId = actor.fieldCardFrames.Count - 1;
                }
                else
                {
                    frameId = FieldSlotToFrameId(actor, fieldSlot);
                    if (frameId < 0)
                    {
                        error = "digivolve target slot " + fieldSlot + " is empty";
                        return null;
                    }
                }
                return new PlayCardAction(card.CardIndex, frameId, new int[0], -1, new int[0]);
            }

            // Activate a [Main] effect on a permanent.
            if (actionId >= Digimon.Recording.ActionSpace.FIELD_EFFECT_START
                && actionId < Digimon.Recording.ActionSpace.FIELD_EFFECT_END)
            {
                int rel = actionId - Digimon.Recording.ActionSpace.FIELD_EFFECT_START;
                int slot = rel / Digimon.Recording.ActionSpace.EFFECTS_PER_PERMANENT;
                int sub  = rel % Digimon.Recording.ActionSpace.EFFECTS_PER_PERMANENT;
                if (sub != Digimon.Recording.ActionSpace.FIELD_EFFECT_SLOT_FOR_MAIN)
                {
                    // ActionEncoder maps DCGO's positional SkillIndex 0 onto
                    // the semantic [Main] sub-slot and refuses anything else;
                    // the inverse has to refuse the same set, or it would queue
                    // an Overclock as though it were a [Main] activation.
                    error = "field-effect sub-slot " + sub + " is not the [Main] slot";
                    return null;
                }
                if (slot >= actor.GetFieldPermanents().Count)
                {
                    error = "field-effect slot " + slot + " is empty";
                    return null;
                }
                return new ActivatePermanentAction(slot, 0);
            }

            error = "action id " + actionId + " has no MainPhaseAction shape";
            return null;
        }

        private static CardSource HandCardAt(Player actor, int handSlot, ref string error)
        {
            if (actor.HandCards == null || handSlot < 0 || handSlot >= actor.HandCards.Count)
            {
                error = "hand slot " + handSlot + " is out of range (hand has "
                        + (actor.HandCards == null ? 0 : actor.HandCards.Count) + ")";
                return null;
            }
            return actor.HandCards[handSlot];
        }

        /// <summary>
        /// COMPACT battle-area position -&gt; SPARSE
        /// <c>FieldCardFrame.FrameID</c>. The inverse of
        /// <c>ActionEncoder.FrameIdToFieldSlot</c>.
        /// </summary>
        private static int FieldSlotToFrameId(Player player, int slot)
        {
            if (player == null || player.FieldPermanents == null) return -1;
            int seen = 0;
            for (int i = 0; i < player.FieldPermanents.Length; i++)
            {
                Permanent p = player.FieldPermanents[i];
                if (p == null || p.TopCard == null) continue;
                if (seen == slot) return i;
                seen++;
            }
            return -1;
        }

        private static string[] ToArray(IList<string> items)
        {
            string[] result = new string[items.Count];
            for (int i = 0; i < items.Count; i++) result[i] = items[i] ?? "";
            return result;
        }
    }
}
