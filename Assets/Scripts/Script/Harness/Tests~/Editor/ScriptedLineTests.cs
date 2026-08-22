using NUnit.Framework;
using Digimon.Harness;

namespace Digimon.Harness.Tests
{
    /// <summary>
    /// Prompt kinds used below are drawn from the closed 13-kind vocabulary
    /// documented on <see cref="PromptContext.Kind"/>: the 10 selection-prompt
    /// class names (SelectCardEffect, SelectHandEffect, SelectPermanentEffect,
    /// SelectAttackEffect, SelectCountEffect, SelectDigiXrosClass,
    /// MultipleSkills, OptionalSkill, generic_int, generic_bool) plus
    /// mulligan / breeding_action / main_phase.
    /// </summary>
    public class ScriptedLineTests
    {
        private static HarnessJobStep Step(int actor, int id, string prompt,
                                           int count = -1, string[] candidates = null)
        {
            return new HarnessJobStep
            {
                actor = actor,
                action_id = id,
                expect_prompt = prompt,
                expect_count = count,
                expect_candidates = candidates ?? new string[0],
            };
        }

        private static PromptContext Ctx(string kind, int count = -1, string[] candidates = null)
        {
            return new PromptContext { Kind = kind, Count = count, Candidates = candidates ?? new string[0] };
        }

        [Test]
        public void MatchingStep_YieldsActionAndAdvances()
        {
            var line = new ScriptedLine(new[] { Step(0, 12, "main_phase"), Step(0, 13, "main_phase") });
            Assert.IsTrue(line.TryTake(0, Ctx("main_phase"), out int id, out string mismatch));
            Assert.AreEqual(12, id);
            Assert.IsNull(mismatch);
            Assert.AreEqual(1, line.Cursor);
        }

        [Test]
        public void WrongActor_IsAMismatchAndDoesNotAdvance()
        {
            var line = new ScriptedLine(new[] { Step(0, 12, "main_phase") });
            Assert.IsFalse(line.TryTake(1, Ctx("main_phase"), out int id, out string mismatch));
            StringAssert.Contains("actor", mismatch);
            Assert.AreEqual(0, line.Cursor);
        }

        [Test]
        public void WrongPromptKind_IsAMismatchAndDoesNotAdvance()
        {
            var line = new ScriptedLine(new[] { Step(0, 12, "main_phase") });
            Assert.IsFalse(line.TryTake(0, Ctx("SelectPermanentEffect"), out int id, out string mismatch));
            StringAssert.Contains("main_phase", mismatch);
            StringAssert.Contains("SelectPermanentEffect", mismatch);
            Assert.AreEqual(0, line.Cursor);
        }

        [Test]
        public void EmptyExpectPrompt_SkipsTheKindAssertion()
        {
            // An empty expect_prompt asserts nothing about the kind, so ANY of
            // the 13 kinds is accepted -- here one the step plainly did not name.
            var line = new ScriptedLine(new[] { Step(0, 12, "") });
            Assert.IsTrue(line.TryTake(0, Ctx("SelectDigiXrosClass"), out int id, out string mismatch));
            Assert.AreEqual(12, id);
        }

        [Test]
        public void CountMismatch_IsAMismatch()
        {
            var line = new ScriptedLine(new[] { Step(0, 12, "SelectPermanentEffect", count: 2) });
            Assert.IsFalse(line.TryTake(0, Ctx("SelectPermanentEffect", count: 1), out int id, out string mismatch));
            StringAssert.Contains("count", mismatch);
        }

        [Test]
        public void NegativeExpectCount_SkipsTheCountAssertion()
        {
            var line = new ScriptedLine(new[] { Step(0, 12, "SelectPermanentEffect", count: -1) });
            Assert.IsTrue(line.TryTake(0, Ctx("SelectPermanentEffect", count: 3), out int id, out string mismatch));
        }

        [Test]
        public void CandidatesCompareAsAMultiset_NotByOrder()
        {
            var line = new ScriptedLine(new[] {
                Step(0, 12, "SelectPermanentEffect", candidates: new[] { "A", "B" }) });
            Assert.IsTrue(line.TryTake(0, Ctx("SelectPermanentEffect", candidates: new[] { "B", "A" }),
                                       out int id, out string mismatch));
        }

        [Test]
        public void CandidateSetMismatch_IsAMismatch()
        {
            var line = new ScriptedLine(new[] {
                Step(0, 12, "SelectPermanentEffect", candidates: new[] { "A", "B" }) });
            Assert.IsFalse(line.TryTake(0, Ctx("SelectPermanentEffect", candidates: new[] { "A", "C" }),
                                        out int id, out string mismatch));
            StringAssert.Contains("candidates", mismatch);
        }

        [Test]
        public void ExhaustedLine_IsAMismatchNotASilentPass()
        {
            // DCGO asking one more question than the line answers means the two
            // engines disagree about how many decisions this position has --
            // exactly the divergence class that never shows up as an illegal
            // action. It must never fall through to AutoSelect.
            var line = new ScriptedLine(new[] { Step(0, 12, "main_phase") });
            line.TryTake(0, Ctx("main_phase"), out _, out _);
            Assert.IsTrue(line.IsExhausted);
            Assert.IsFalse(line.TryTake(0, Ctx("main_phase"), out int id, out string mismatch));
            StringAssert.Contains("exhausted", mismatch);
        }

        [Test]
        public void EmptyLine_IsExhaustedImmediately()
        {
            var line = new ScriptedLine(new HarnessJobStep[0]);
            Assert.IsTrue(line.IsExhausted);
        }

        // -- TryTakeStep: the whole-step path the selection hooks use -----

        [Test]
        public void TryTakeStep_ReturnsTheWholeStepAndAdvances()
        {
            var selection = new HarnessJobStep
            {
                actor = 0,
                expect_prompt = "SelectHandEffect",
                expect_candidates = new string[0],
                select_card_ids = new[] { "ST1-03", "ST1-03" },
            };
            var line = new ScriptedLine(new[] { selection });
            Assert.IsTrue(line.TryTakeStep(0, Ctx("SelectHandEffect"),
                                           out HarnessJobStep step, out string mismatch));
            Assert.IsNull(mismatch);
            Assert.AreSame(selection, step);
            CollectionAssert.AreEqual(new[] { "ST1-03", "ST1-03" }, step.select_card_ids);
            Assert.AreEqual(1, line.Cursor);
        }

        [Test]
        public void TryTakeStep_Mismatch_YieldsNoStepAndDoesNotAdvance()
        {
            var line = new ScriptedLine(new[] { Step(0, 12, "SelectHandEffect") });
            Assert.IsFalse(line.TryTakeStep(0, Ctx("SelectCountEffect"),
                                            out HarnessJobStep step, out string mismatch));
            Assert.IsNull(step);
            StringAssert.Contains("SelectHandEffect", mismatch);
            Assert.AreEqual(0, line.Cursor);
        }

        [Test]
        public void TryTake_StillReadsActionIdOffTheStep()
        {
            // The int path is a wrapper over TryTakeStep -- one consume path,
            // the int overload just reads step.action_id.
            var line = new ScriptedLine(new[] { Step(0, 61, "breeding_action") });
            Assert.IsTrue(line.TryTake(0, Ctx("breeding_action"), out int id, out string mismatch));
            Assert.AreEqual(61, id);
            Assert.AreEqual(1, line.Cursor);
        }
    }
}
