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

        // -- A selection that cannot complete its prompt ------------------
        // The EX7-073 scope-neg job (2026-10-06) wedged the player for 15+
        // minutes: DCGO's SelectCardEffect wanted 2 cards or a cancel, the
        // step named one. Both failure messages below are the
        // "selection did not complete" member of the prompt-mismatch family,
        // which dcgo-harness and the card loop parse.

        private static HarnessJobStep OptionalYes()
        {
            return new HarnessJobStep
            {
                actor = 0,
                expect_prompt = InputDriver.KindOptionalSkill,
                select_has_bool = true,
                select_bool = true,
            };
        }

        private static HarnessJobStep PickBeelStarmon()
        {
            return new HarnessJobStep
            {
                actor = 0,
                expect_prompt = InputDriver.KindSelectCard,
                select_card_ids = new[] { "BT25-085" },
            };
        }

        [Test]
        public void AbortIncomplete_NamesTheStepJustConsumed()
        {
            InputDriver.Install(Job(OptionalYes(), PickBeelStarmon()));
            InputDriver.TryAnswerStep(0, InputDriver.KindOptionalSkill, 1, null, out HarnessJobStep _);
            Assert.IsTrue(InputDriver.TryAnswerStep(
                0, InputDriver.KindSelectCard, 2, new[] { "BT25-085" }, out HarnessJobStep pick));

            LogAssert.Expect(LogType.Error, new Regex(Regex.Escape(
                "prompt mismatch: step 1 selection did not complete 'SelectCardEffect' " +
                "(wanted [BT25-085], prompt needs 2 picks or cancel)")));
            InputDriver.AbortIncomplete(InputDriver.KindSelectCard, pick, "prompt needs 2 picks or cancel");
            Assert.IsFalse(InputDriver.IsActive);
        }

        [Test]
        public void Stall_NamesThePendingStepThePromptNeverReached()
        {
            // The scope-neg shape: step 0 answered, then DCGO opened step 1's
            // prompt without ever consulting the script.
            InputDriver.Install(Job(OptionalYes(), PickBeelStarmon()));
            InputDriver.TryAnswerStep(0, InputDriver.KindOptionalSkill, 1, null, out HarnessJobStep _);

            InputDriver.CheckStall(100f, "board", true);
            Assert.IsTrue(InputDriver.IsActive);

            LogAssert.Expect(LogType.Error, new Regex(Regex.Escape(
                "prompt mismatch: step 1 selection did not complete 'SelectCardEffect' " +
                "(wanted [BT25-085], DCGO held a prompt open for 10s after step 0 " +
                "with no new prompt and no game progress)")));
            InputDriver.CheckStall(110f, "board", true);
            Assert.IsFalse(InputDriver.IsActive);
        }

        [Test]
        public void Stall_AfterTheLastStep_NamesTheLastStep()
        {
            InputDriver.Install(Job(PickBeelStarmon()));
            InputDriver.TryAnswerStep(0, InputDriver.KindSelectCard, 2, new[] { "BT25-085" }, out HarnessJobStep _);

            InputDriver.CheckStall(0f, "board", true);
            LogAssert.Expect(LogType.Error, new Regex(Regex.Escape(
                "prompt mismatch: step 0 selection did not complete 'SelectCardEffect' " +
                "(wanted [BT25-085], DCGO held a prompt open for 10s after the line's last step " +
                "with no new prompt and no game progress)")));
            InputDriver.CheckStall(10f, "board", true);
            Assert.IsFalse(InputDriver.IsActive);
        }

        [Test]
        public void Stall_ANonCardAnswerIsDescribed()
        {
            InputDriver.Install(Job(PickBeelStarmon(), OptionalYes()));
            InputDriver.TryAnswerStep(0, InputDriver.KindSelectCard, 1, new[] { "BT25-085" }, out HarnessJobStep _);

            InputDriver.CheckStall(0f, "board", true);
            LogAssert.Expect(LogType.Error, new Regex(Regex.Escape(
                "prompt mismatch: step 1 selection did not complete 'OptionalSkill' " +
                "(wanted [], answer select_bool=true; DCGO held a prompt open for 10s after step 0 " +
                "with no new prompt and no game progress)")));
            InputDriver.CheckStall(10f, "board", true);
        }

        [Test]
        public void Stall_GameProgressKeepsTheLineAlive()
        {
            InputDriver.Install(Job(OptionalYes(), PickBeelStarmon()));
            InputDriver.TryAnswerStep(0, InputDriver.KindOptionalSkill, 1, null, out HarnessJobStep _);

            InputDriver.CheckStall(0f, "board-1", true);
            InputDriver.CheckStall(9f, "board-2", true);
            InputDriver.CheckStall(18f, "board-2", true);
            Assert.IsTrue(InputDriver.IsActive);
        }

        [Test]
        public void Stall_IsANoOpWithNoScriptedLine()
        {
            InputDriver.Release();
            InputDriver.CheckStall(0f, "board", true);
            InputDriver.CheckStall(1000f, "board", true);
            Assert.IsFalse(InputDriver.IsActive);
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

        // -- the <Link> declaration recogniser -----------------------------
        // BuildMainPhaseAction resolves our SEMANTIC link sub-slot
        // (FIELD_EFFECT_SLOT_FOR_LINK) to the POSITIONAL index of the
        // LinkEffect ActivateClass in the permanent's OnDeclaration list, and
        // the [Main] sub-slot to the first activatable effect that is NOT it.
        // Both hinge on this predicate, which is exercised here without a
        // game: an ActivateClass set up exactly as CardEffectFactory.LinkEffect
        // sets one up (same description stamp, same name prefix).

        private static ActivateClass ActivateClassNamed(string name, string description)
        {
            ActivateClass a = new ActivateClass();
            a.SetUpICardEffect(name, _ => true, null);
            a.SetUpActivateClass(null, null, -1, true, description);
            return a;
        }

        [Test]
        public void IsLinkDeclaration_RecognisesTheLinkEffectByItsDescriptionStamp()
        {
            ActivateClass link = ActivateClassNamed(
                "Link (Cost: 2)", DataBase.LinkEffectDiscription());
            Assert.IsTrue(InputDriver.IsLinkDeclaration(link));
        }

        [Test]
        public void IsLinkDeclaration_FallsBackToTheNamePrefix()
        {
            // A hand-built declaration with a different description still
            // counts by its "Link (Cost: N)" name -- the fallback exists so a
            // card script that does not go through the factory is not
            // misfiled as a [Main] ability.
            ActivateClass link = ActivateClassNamed("Link (Cost: 0)", "custom text");
            Assert.IsTrue(InputDriver.IsLinkDeclaration(link));
        }

        [Test]
        public void IsLinkDeclaration_RejectsAnOrdinaryMainAbility()
        {
            ActivateClass main = ActivateClassNamed(
                "[Main] Digiburst 1", "[Main] <Digiburst 1> (Trash 1 card ...)");
            Assert.IsFalse(InputDriver.IsLinkDeclaration(main));
            Assert.IsFalse(InputDriver.IsLinkDeclaration(null));
        }
    }
}
