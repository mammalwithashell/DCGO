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
