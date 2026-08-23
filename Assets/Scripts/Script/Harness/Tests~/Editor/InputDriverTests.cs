using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Digimon.Harness;

namespace Digimon.Harness.Tests
{
    /// <summary>
    /// The driver's step-consume seam, exercised without a game: the int path
    /// and the step path share one consume path, and a payload/prompt shape
    /// mismatch aborts as a finding. With no JobWatcher in EditMode, an abort
    /// surfaces as the driver's own LogError -- which is what these tests
    /// assert on.
    /// </summary>
    public class InputDriverTests
    {
        [TearDown]
        public void ReleaseDriver()
        {
            InputDriver.Release();
        }

        private static HarnessJob Job(params HarnessJobStep[] steps)
        {
            return new HarnessJob
            {
                job_id = "input-driver-tests",
                policy = "scripted",
                decks = new HarnessJobDecks { p0 = new string[0], p1 = new string[0] },
                inputs = steps,
            };
        }

        [Test]
        public void TryAnswerStep_ReturnsTheWholeSelectionStep()
        {
            InputDriver.Install(Job(new HarnessJobStep
            {
                actor = 0,
                expect_prompt = InputDriver.KindSelectHand,
                select_card_ids = new[] { "ST1-03" },
            }));

            Assert.IsTrue(InputDriver.TryAnswerStep(
                0, InputDriver.KindSelectHand, 1, new[] { "ST1-03" },
                out HarnessJobStep step));
            CollectionAssert.AreEqual(new[] { "ST1-03" }, step.select_card_ids);
            Assert.AreEqual(1, InputDriver.Cursor);
        }

        [Test]
        public void SelectionStepAtActionIdPrompt_AbortsAsAPromptMismatch()
        {
            // The author scripted a selection answer, but DCGO asked a
            // main-phase question. Answering step.action_id (default 0) would
            // silently queue a nonsense action; it must abort instead.
            InputDriver.Install(Job(new HarnessJobStep
            {
                actor = 0,
                select_card_ids = new[] { "ST1-03" },
            }));

            LogAssert.Expect(LogType.Error, new Regex("selection payload"));
            Assert.IsFalse(InputDriver.TryAnswer(
                0, InputDriver.KindMainPhase, -1, null, out int actionId));
            Assert.IsFalse(InputDriver.IsActive);
        }

        [Test]
        public void ActionIdStepAtActionIdPrompt_StillAnswers()
        {
            InputDriver.Install(Job(new HarnessJobStep
            {
                actor = 0,
                action_id = 61,
                expect_prompt = InputDriver.KindBreedingAction,
            }));

            Assert.IsTrue(InputDriver.TryAnswer(
                0, InputDriver.KindBreedingAction, 1, null, out int actionId));
            Assert.AreEqual(61, actionId);
        }

        [Test]
        public void PromptKindMismatch_AbortsOnTheStepPathToo()
        {
            InputDriver.Install(Job(new HarnessJobStep
            {
                actor = 0,
                expect_prompt = InputDriver.KindSelectCount,
                select_value = 2,
            }));

            LogAssert.Expect(LogType.Error, new Regex("prompt mismatch"));
            Assert.IsFalse(InputDriver.TryAnswerStep(
                0, InputDriver.KindOptionalSkill, 1, null, out HarnessJobStep step));
            Assert.IsFalse(InputDriver.IsActive);
        }

        // -- MultipleSkills (trigger order) -------------------------------
        // The prompt now offers its candidates (each stacked trigger's SOURCE
        // CARD id), so a step can name the trigger by identity instead of by
        // DCGO's list index. The index-to-answer resolution itself is
        // SelectionAnswer.MatchOneWithOrdinal; what these cover is that the
        // driver seam carries the candidate list and the identity payload.

        [Test]
        public void MultipleSkillsStep_NamesItsTriggerByCardIdentity()
        {
            InputDriver.Install(Job(new HarnessJobStep
            {
                actor = 0,
                expect_prompt = InputDriver.KindMultipleSkills,
                expect_candidates = new[] { "EX12-047", "EX12-011" },
                select_card_ids = new[] { "EX12-047" },
            }));

            Assert.IsTrue(InputDriver.TryAnswerStep(
                0, InputDriver.KindMultipleSkills, 1, new[] { "EX12-047", "EX12-011" },
                out HarnessJobStep step));
            CollectionAssert.AreEqual(new[] { "EX12-047" }, step.select_card_ids);
            // No ordinal supplied: the absent sentinel is what reaches the matcher.
            Assert.AreEqual(SelectionAnswer.NoOrdinal, step.select_ordinal);
        }

        [Test]
        public void MultipleSkillsStep_SameCardTwice_CarriesTheOrdinalAlongsideTheIdentity()
        {
            // One deleted carrier stacking two triggers offers its identity
            // twice; the step names the card AND which of its triggers.
            InputDriver.Install(Job(new HarnessJobStep
            {
                actor = 0,
                expect_prompt = InputDriver.KindMultipleSkills,
                select_card_ids = new[] { "EX12-047" },
                select_ordinal = 1,
            }));

            Assert.IsTrue(InputDriver.TryAnswerStep(
                0, InputDriver.KindMultipleSkills, 1, new[] { "EX12-047", "EX12-047" },
                out HarnessJobStep step));
            Assert.AreEqual(1, step.select_ordinal);
            // The raw-index fallback stays absent: an identity answer and an
            // index answer never travel together.
            Assert.AreEqual(SelectionAnswer.NoOrdinal, step.select_value);

            Assert.IsTrue(SelectionAnswer.MatchOneWithOrdinal(
                step.select_card_ids[0], step.select_ordinal,
                new[] { "EX12-047", "EX12-047" }, out int pick, out string error));
            Assert.IsNull(error);
            Assert.AreEqual(1, pick);
        }

        [Test]
        public void MultipleSkillsStep_CandidateMismatch_AbortsAsAFinding()
        {
            // "DCGO did not stack the triggers our engine stacked" is exactly
            // the divergence the exam is looking for, and it must surface
            // BEFORE an answer is fed, not as a state diff twenty rows later.
            InputDriver.Install(Job(new HarnessJobStep
            {
                actor = 0,
                expect_prompt = InputDriver.KindMultipleSkills,
                expect_candidates = new[] { "EX12-047", "EX12-011" },
                select_card_ids = new[] { "EX12-047" },
            }));

            LogAssert.Expect(LogType.Error, new Regex("expected candidates"));
            Assert.IsFalse(InputDriver.TryAnswerStep(
                0, InputDriver.KindMultipleSkills, 1, new[] { "EX12-047" },
                out HarnessJobStep step));
            Assert.IsFalse(InputDriver.IsActive);
        }

        [Test]
        public void MultipleSkillsStep_RawIndexFallback_StillArrives()
        {
            // select_value alone remains expressible (a raw 0-based DCGO skill
            // index). The RANGE check lives at the hook, which is the only
            // place that knows how many effects the prompt stacked.
            InputDriver.Install(Job(new HarnessJobStep
            {
                actor = 0,
                expect_prompt = InputDriver.KindMultipleSkills,
                select_value = 0,
            }));

            Assert.IsTrue(InputDriver.TryAnswerStep(
                0, InputDriver.KindMultipleSkills, 1, new[] { "EX12-047", "EX12-011" },
                out HarnessJobStep step));
            Assert.AreEqual(0, step.select_value);
            Assert.AreEqual(0, step.select_card_ids.Length);
        }

        [Test]
        public void MultipleSkillsStep_BareOrdinal_IsStillASelectionStep()
        {
            // An ordinal with no identity is meaningless at the hook (which
            // aborts naming what it needs), but it must never read as an
            // action-id step and get queued as a main-phase action.
            var step = new HarnessJobStep { actor = 0, select_ordinal = 0 };
            Assert.IsTrue(step.IsSelection);

            InputDriver.Install(Job(step));
            LogAssert.Expect(LogType.Error, new Regex("selection payload"));
            Assert.IsFalse(InputDriver.TryAnswer(
                0, InputDriver.KindMainPhase, -1, null, out int actionId));
            Assert.IsFalse(InputDriver.IsActive);
        }

        [Test]
        public void MultipleSkillsStep_Cancel_IsASelectionPayload_NotAnActionId()
        {
            // DCGO's "Don't activate these effects" (-1) travels as select_cancel,
            // never as a magic select_value, so it can never be confused with an
            // index the range check would have to special-case.
            InputDriver.Install(Job(new HarnessJobStep
            {
                actor = 0,
                expect_prompt = InputDriver.KindMultipleSkills,
                select_cancel = true,
            }));

            Assert.IsTrue(InputDriver.TryAnswerStep(
                0, InputDriver.KindMultipleSkills, 1, new[] { "EX12-047", "EX12-011" },
                out HarnessJobStep step));
            Assert.IsTrue(step.select_cancel);
            Assert.IsTrue(step.IsSelection);
        }

        [Test]
        public void ExhaustedLine_CompletesQuietly_OnTheStepPath()
        {
            InputDriver.Install(Job(new HarnessJobStep
            {
                actor = 0,
                expect_prompt = InputDriver.KindOptionalSkill,
                select_has_bool = true,
                select_bool = true,
            }));

            Assert.IsTrue(InputDriver.TryAnswerStep(
                0, InputDriver.KindOptionalSkill, 1, null, out HarnessJobStep step));
            Assert.IsTrue(step.select_bool);

            // Running off the end is normal termination, not a finding: no
            // error is logged, and the driver deactivates.
            Assert.IsFalse(InputDriver.TryAnswerStep(
                0, InputDriver.KindOptionalSkill, 1, null, out HarnessJobStep after));
            Assert.IsFalse(InputDriver.IsActive);
        }
    }
}
