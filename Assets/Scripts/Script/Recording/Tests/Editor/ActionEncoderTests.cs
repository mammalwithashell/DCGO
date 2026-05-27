using NUnit.Framework;
using Digimon.Recording;

namespace Digimon.Recording.Tests
{
    /// <summary>
    /// EditMode unit tests for <see cref="ActionEncoder"/>. The encoder is
    /// pure-function for the cases tested here (mulligan, Pass, ActivateCard,
    /// PlayCard base); the Attack and ActivatePermanent paths require a
    /// configured <c>Player</c> with field permanents, so they are exercised
    /// by integration tests on actual bot-match recordings — out of scope
    /// for EditMode tests.
    /// </summary>
    /// <remarks>
    /// Test naming convention: <c>{Method}_{Input}_{ExpectedOutcome}</c>.
    /// Each test corresponds to one row in the encoding contract table —
    /// edits to the encoder must keep these green or update the table.
    /// </remarks>
    public class ActionEncoderTests
    {
        // ── Mulligan (action IDs 0 and 1) ────────────────────────────────

        [Test]
        public void Mulligan_Keep_EncodesZero()
        {
            var r = ActionEncoder.EncodeMulligan(redrew: false);
            Assert.IsFalse(r.IsFailure);
            Assert.AreEqual(0, r.ActionId);
        }

        [Test]
        public void Mulligan_Redraw_EncodesOne()
        {
            var r = ActionEncoder.EncodeMulligan(redrew: true);
            Assert.IsFalse(r.IsFailure);
            Assert.AreEqual(1, r.ActionId);
        }

        // ── PassAction (action ID 62) ────────────────────────────────────

        [Test]
        public void PassAction_EncodesAsPASS()
        {
            var r = ActionEncoder.EncodeMainPhaseAction(
                actorPlayerId: 0, action: new PassAction(), actor: null);
            Assert.IsFalse(r.IsFailure);
            Assert.AreEqual(ActionSpace.PASS, r.ActionId);
            Assert.AreEqual(62, r.ActionId);
        }

        // ── PlayCardAction base (action IDs 0..29) ───────────────────────
        // PlayCard's `targetFrameID`, `jogress`, `burst`, `appfuse` extras
        // are NOT part of the base encoding — they decompose into separate
        // rows via DecomposePlayCardExtras (tested separately below).

        [Test]
        public void PlayCard_HandIndex0_EncodesAsActionId0()
        {
            var action = new PlayCardAction(
                cardIndex: 0, targetFrameID: 0, jogressEvoRootsFrameIDs: new int[0],
                burstTamerFrameID: -1, appFusionFrameIDs: new int[0]);
            var r = ActionEncoder.EncodeMainPhaseAction(0, action, actor: null);
            Assert.IsFalse(r.IsFailure);
            Assert.AreEqual(0, r.ActionId);
        }

        [Test]
        public void PlayCard_HandIndex5_EncodesAsActionId5()
        {
            var action = new PlayCardAction(
                cardIndex: 5, targetFrameID: 0, jogressEvoRootsFrameIDs: new int[0],
                burstTamerFrameID: -1, appFusionFrameIDs: new int[0]);
            var r = ActionEncoder.EncodeMainPhaseAction(0, action, actor: null);
            Assert.IsFalse(r.IsFailure);
            Assert.AreEqual(5, r.ActionId);
        }

        [Test]
        public void PlayCard_HandIndex29_EncodesAsActionId29()
        {
            var action = new PlayCardAction(
                cardIndex: 29, targetFrameID: 0, jogressEvoRootsFrameIDs: new int[0],
                burstTamerFrameID: -1, appFusionFrameIDs: new int[0]);
            var r = ActionEncoder.EncodeMainPhaseAction(0, action, actor: null);
            Assert.IsFalse(r.IsFailure);
            Assert.AreEqual(29, r.ActionId);
        }

        [Test]
        public void PlayCard_HandIndexNegative_EncodesAsFailure()
        {
            var action = new PlayCardAction(
                cardIndex: -1, targetFrameID: 0, jogressEvoRootsFrameIDs: new int[0],
                burstTamerFrameID: -1, appFusionFrameIDs: new int[0]);
            var r = ActionEncoder.EncodeMainPhaseAction(0, action, actor: null);
            Assert.IsTrue(r.IsFailure);
            Assert.AreEqual("play_card_hand_index_out_of_range", r.FailureReason);
        }

        [Test]
        public void PlayCard_HandIndexAt30_EncodesAsFailure()
        {
            // 30 is PLAY_HAND_END (exclusive); cardIndex must be < 30.
            var action = new PlayCardAction(
                cardIndex: 30, targetFrameID: 0, jogressEvoRootsFrameIDs: new int[0],
                burstTamerFrameID: -1, appFusionFrameIDs: new int[0]);
            var r = ActionEncoder.EncodeMainPhaseAction(0, action, actor: null);
            Assert.IsTrue(r.IsFailure);
        }

        // ── ActivateCardAction (HAND_EFFECT range 30..60) ────────────────

        [Test]
        public void ActivateCard_HandIndex0_Skill0_EncodesAs30()
        {
            var action = new ActivateCardAction(cardIndex: 0, skillIndex: 0);
            var r = ActionEncoder.EncodeMainPhaseAction(0, action, actor: null);
            Assert.IsFalse(r.IsFailure);
            Assert.AreEqual(30, r.ActionId);
            Assert.AreEqual(ActionSpace.HAND_EFFECT_START, r.ActionId);
        }

        [Test]
        public void ActivateCard_HandIndex5_Skill0_EncodesAs35()
        {
            var action = new ActivateCardAction(cardIndex: 5, skillIndex: 0);
            var r = ActionEncoder.EncodeMainPhaseAction(0, action, actor: null);
            Assert.IsFalse(r.IsFailure);
            Assert.AreEqual(35, r.ActionId);
        }

        [Test]
        public void ActivateCard_NonzeroSkill_EncodesAsFailure()
        {
            // Hand cards rarely have >1 [Hand][Main] effect; current action
            // space doesn't model per-skill sub-slots in the hand range.
            var action = new ActivateCardAction(cardIndex: 3, skillIndex: 1);
            var r = ActionEncoder.EncodeMainPhaseAction(0, action, actor: null);
            Assert.IsTrue(r.IsFailure);
            Assert.AreEqual("activate_card_nonzero_skill_index", r.FailureReason);
        }

        [Test]
        public void ActivateCard_HandIndexOutOfRange_EncodesAsFailure()
        {
            var action = new ActivateCardAction(cardIndex: 30, skillIndex: 0);
            var r = ActionEncoder.EncodeMainPhaseAction(0, action, actor: null);
            Assert.IsTrue(r.IsFailure);
            Assert.AreEqual("activate_card_hand_index_out_of_range", r.FailureReason);
        }

        // ── CheatAction (debug-only; should fail encoding) ───────────────

        [Test]
        public void CheatAction_EncodesAsFailure()
        {
            var action = new CheatAction(playerID: 0, cheatType: CheatAction.Type.Draw);
            var r = ActionEncoder.EncodeMainPhaseAction(0, action, actor: null);
            Assert.IsTrue(r.IsFailure);
            Assert.AreEqual("cheat_action_unsupported", r.FailureReason);
        }

        // ── PlayCard extras decomposition ────────────────────────────────

        [Test]
        public void PlayCard_NoExtras_DecomposesToEmpty()
        {
            var action = new PlayCardAction(
                cardIndex: 0, targetFrameID: 0, jogressEvoRootsFrameIDs: new int[0],
                burstTamerFrameID: -1, appFusionFrameIDs: new int[0]);
            int count = 0;
            foreach (var _ in ActionEncoder.DecomposePlayCardExtras(action, 0))
                count++;
            Assert.AreEqual(0, count);
        }

        [Test]
        public void PlayCard_WithBurstTamer_DecomposesToOneSourceSelect()
        {
            // Burst tamer at frame 3 → encode_source_select(3, 0)
            //   = SOURCE_SELECT_START + 3 * SOURCES_PER_FIELD + 0
            //   = 2000 + 3 * 12 + 0 = 2036.
            var action = new PlayCardAction(
                cardIndex: 0, targetFrameID: 0, jogressEvoRootsFrameIDs: new int[0],
                burstTamerFrameID: 3, appFusionFrameIDs: new int[0]);
            int count = 0;
            ushort id = 0;
            foreach (var e in ActionEncoder.DecomposePlayCardExtras(action, 0))
            {
                count++;
                Assert.IsFalse(e.IsFailure);
                id = e.ActionId;
            }
            Assert.AreEqual(1, count);
            Assert.AreEqual(2036, id);
        }

        [Test]
        public void PlayCard_WithAppFusion_DecomposesPairsToSourceSelects()
        {
            // AppFusion array layout: [frame0, slot0, frame1, slot1, ...]
            // (frame=2, slot=1) → 2000 + 2*12 + 1 = 2025
            // (frame=4, slot=3) → 2000 + 4*12 + 3 = 2051
            var action = new PlayCardAction(
                cardIndex: 0, targetFrameID: 0, jogressEvoRootsFrameIDs: new int[0],
                burstTamerFrameID: -1, appFusionFrameIDs: new int[] { 2, 1, 4, 3 });
            var ids = new System.Collections.Generic.List<ushort>();
            foreach (var e in ActionEncoder.DecomposePlayCardExtras(action, 0))
            {
                Assert.IsFalse(e.IsFailure);
                ids.Add(e.ActionId);
            }
            Assert.AreEqual(2, ids.Count);
            Assert.AreEqual(2025, ids[0]);
            Assert.AreEqual(2051, ids[1]);
        }

        [Test]
        public void PlayCard_WithJogress_DecomposesToPendingEncodingFailures()
        {
            // Jogress encoding is still pending action-space support; we
            // surface as failures rather than silently dropping picks.
            var action = new PlayCardAction(
                cardIndex: 0, targetFrameID: 0, jogressEvoRootsFrameIDs: new int[] { 5, 7 },
                burstTamerFrameID: -1, appFusionFrameIDs: new int[0]);
            int failures = 0;
            foreach (var e in ActionEncoder.DecomposePlayCardExtras(action, 0))
            {
                if (e.IsFailure) failures++;
            }
            Assert.AreEqual(2, failures);
        }

        // ── Selections: Phase 1 fallback contract ────────────────────────

        [Test]
        public void SelectionInt_EncodesAsFailureWithRawValue()
        {
            // Until prompt identity is plumbed from each Select*Effect, the
            // encoder emits a structured failure carrying the raw value so
            // the replay harness can identify divergence points.
            var r = ActionEncoder.EncodeSelectionInt(0, value: 7, phaseName: "Main");
            Assert.IsTrue(r.IsFailure);
            Assert.AreEqual("selection_prompt_kind_unknown", r.FailureReason);
            StringAssert.Contains("int_value=7", r.RawDebugValue);
        }

        [Test]
        public void SelectionBool_EncodesAsFailureWithRawValue()
        {
            var r = ActionEncoder.EncodeSelectionBool(1, value: true, phaseName: "Main");
            Assert.IsTrue(r.IsFailure);
            Assert.AreEqual("selection_prompt_kind_unknown", r.FailureReason);
            StringAssert.Contains("bool_value=True", r.RawDebugValue);
        }

        // ── Encoded value-type semantics ─────────────────────────────────

        [Test]
        public void Encoded_OkAndFail_AreDistinguishable()
        {
            var ok = ActionEncoder.Encoded.Ok(42);
            var fail = ActionEncoder.Encoded.Fail("reason", "raw");
            Assert.IsFalse(ok.IsFailure);
            Assert.IsTrue(fail.IsFailure);
            Assert.AreEqual(42, ok.ActionId);
            Assert.AreEqual("reason", fail.FailureReason);
            Assert.AreEqual("raw", fail.RawDebugValue);
        }
    }
}
