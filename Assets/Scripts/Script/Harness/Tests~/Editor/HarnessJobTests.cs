using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Digimon.Harness;

namespace Digimon.Harness.Tests
{
    public class HarnessJobTests
    {
        private const string Phase1Job = @"{
            ""job_id"": ""vol-00042"",
            ""policy"": ""ai"",
            ""decks"": { ""p0"": [""EX12-035""], ""p1"": [""BT16-082""] },
            ""first_player"": 0,
            ""seed"": 424242,
            ""limits"": { ""max_turns"": 40, ""timeout_seconds"": 180 }
        }";

        // expect_prompt is drawn from the closed 13-kind prompt vocabulary
        // documented on PromptContext.Kind -- so "SelectPermanentEffect", the
        // class name the recorder writes, not a hand-invented slug.
        private const string ScriptedJob = @"{
            ""job_id"": ""exam-EX12-035-0"",
            ""policy"": ""scripted"",
            ""decks"": { ""p0"": [""EX12-035""], ""p1"": [""BT16-082""] },
            ""deck_order"": { ""p0"": [""ST1-02"", ""EX12-035""], ""p1"": [] },
            ""inputs"": [
                { ""actor"": 0, ""action_id"": 12, ""expect_prompt"": ""main_phase"" },
                { ""actor"": 0, ""action_id"": 1150, ""expect_prompt"": ""SelectPermanentEffect"", ""expect_count"": 1 }
            ],
            ""first_player"": 0,
            ""seed"": 424242,
            ""limits"": { ""max_turns"": 40, ""timeout_seconds"": 180 }
        }";

        [Test]
        public void Phase1Job_StillParses()
        {
            HarnessJob job = HarnessJob.Parse(Phase1Job);
            Assert.IsNotNull(job);
            Assert.AreEqual("vol-00042", job.job_id);
            Assert.AreEqual("ai", job.policy);
        }

        [Test]
        public void Phase1Job_IsNotScripted()
        {
            HarnessJob job = HarnessJob.Parse(Phase1Job);
            Assert.IsFalse(job.IsScripted);
        }

        [Test]
        public void Phase1Job_HasNoDeckOrderOrInputs()
        {
            HarnessJob job = HarnessJob.Parse(Phase1Job);
            // Absent arrays must normalize to empty, never null: every consumer
            // would otherwise need its own null guard, and one missing guard is
            // a NullReferenceException mid-game.
            Assert.IsNotNull(job.inputs);
            Assert.AreEqual(0, job.inputs.Length);
            Assert.IsNotNull(job.deck_order);
            Assert.AreEqual(0, job.deck_order.p0.Length);
        }

        [Test]
        public void ScriptedJob_IsScripted()
        {
            HarnessJob job = HarnessJob.Parse(ScriptedJob);
            Assert.IsNotNull(job);
            Assert.IsTrue(job.IsScripted);
        }

        [Test]
        public void ScriptedJob_ParsesDeckOrder()
        {
            HarnessJob job = HarnessJob.Parse(ScriptedJob);
            CollectionAssert.AreEqual(new[] { "ST1-02", "EX12-035" }, job.deck_order.p0);
            Assert.AreEqual(0, job.deck_order.p1.Length);
        }

        [Test]
        public void ScriptedJob_ParsesInputs()
        {
            HarnessJob job = HarnessJob.Parse(ScriptedJob);
            Assert.AreEqual(2, job.inputs.Length);
            Assert.AreEqual(0, job.inputs[0].actor);
            Assert.AreEqual(12, job.inputs[0].action_id);
            Assert.AreEqual("main_phase", job.inputs[0].expect_prompt);
            Assert.AreEqual("SelectPermanentEffect", job.inputs[1].expect_prompt);
            Assert.AreEqual(1, job.inputs[1].expect_count);
        }

        // A scripted job carrying each selection-payload form the exam wire
        // can express: identity picks (with a duplicate, since occurrence-
        // order matching is the documented contract), a count value, a bool,
        // a cancel -- plus a plain action-id step to prove absence defaults.
        private const string SelectionJob = @"{
            ""job_id"": ""exam-select-forms"",
            ""policy"": ""scripted"",
            ""decks"": { ""p0"": [""EX12-035""], ""p1"": [""BT16-082""] },
            ""inputs"": [
                { ""actor"": 0, ""action_id"": 12, ""expect_prompt"": ""main_phase"" },
                { ""actor"": 0, ""expect_prompt"": ""SelectHandEffect"", ""select_card_ids"": [""ST1-02"", ""ST1-02""] },
                { ""actor"": 0, ""expect_prompt"": ""SelectCountEffect"", ""select_value"": 3 },
                { ""actor"": 1, ""expect_prompt"": ""OptionalSkill"", ""select_has_bool"": true, ""select_bool"": true },
                { ""actor"": 1, ""expect_prompt"": ""SelectPermanentEffect"", ""select_cancel"": true }
            ],
            ""first_player"": 0,
            ""seed"": 424242,
            ""limits"": { ""max_turns"": 40, ""timeout_seconds"": 180 }
        }";

        [Test]
        public void SelectionJob_ActionIdStep_IsNotASelection()
        {
            HarnessJob job = HarnessJob.Parse(SelectionJob);
            Assert.IsNotNull(job);
            Assert.IsFalse(job.inputs[0].IsSelection);
            // Absent selection fields keep their absent defaults: JsonUtility
            // only overwrites a field initializer when the key is present.
            Assert.AreEqual(int.MinValue, job.inputs[0].select_value);
            Assert.IsFalse(job.inputs[0].select_has_bool);
            Assert.IsFalse(job.inputs[0].select_cancel);
            Assert.IsNotNull(job.inputs[0].select_card_ids);
            Assert.AreEqual(0, job.inputs[0].select_card_ids.Length);
        }

        [Test]
        public void SelectionJob_CardIdsForm_ParsesInOrder()
        {
            HarnessJob job = HarnessJob.Parse(SelectionJob);
            Assert.IsTrue(job.inputs[1].IsSelection);
            CollectionAssert.AreEqual(new[] { "ST1-02", "ST1-02" }, job.inputs[1].select_card_ids);
            Assert.AreEqual(int.MinValue, job.inputs[1].select_value);
        }

        [Test]
        public void SelectionJob_ValueForm_Parses()
        {
            HarnessJob job = HarnessJob.Parse(SelectionJob);
            Assert.IsTrue(job.inputs[2].IsSelection);
            Assert.AreEqual(3, job.inputs[2].select_value);
        }

        [Test]
        public void SelectionJob_BoolForm_Parses()
        {
            HarnessJob job = HarnessJob.Parse(SelectionJob);
            Assert.IsTrue(job.inputs[3].IsSelection);
            Assert.IsTrue(job.inputs[3].select_has_bool);
            Assert.IsTrue(job.inputs[3].select_bool);
        }

        [Test]
        public void SelectionJob_CancelForm_Parses()
        {
            HarnessJob job = HarnessJob.Parse(SelectionJob);
            Assert.IsTrue(job.inputs[4].IsSelection);
            Assert.IsTrue(job.inputs[4].select_cancel);
            Assert.AreEqual(0, job.inputs[4].select_card_ids.Length);
        }

        [Test]
        public void ScriptedPolicyWithNoInputs_IsRejected()
        {
            // A scripted job with an empty line would start a game nobody
            // drives and hang until the timeout -- indistinguishable from a
            // hung Unity. Reject it at parse time instead.
            string bad = ScriptedJob.Replace(
                @"""inputs"": [
                { ""actor"": 0, ""action_id"": 12, ""expect_prompt"": ""main_phase"" },
                { ""actor"": 0, ""action_id"": 1150, ""expect_prompt"": ""SelectPermanentEffect"", ""expect_count"": 1 }
            ],", @"""inputs"": [],");
            // The rejection is a Debug.LogError by design (a scripted job with
            // no line is an authoring bug that must be loud, not a shrug). The
            // Unity Test Framework fails any test that logs an error unless the
            // error is declared expected, so declare it.
            LogAssert.Expect(LogType.Error, "[Harness] scripted job exam-EX12-035-0 carries no inputs");
            Assert.IsNull(HarnessJob.Parse(bad));
        }
    }
}
