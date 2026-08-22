using NUnit.Framework;
using Digimon.Harness;

namespace Digimon.Harness.Tests
{
    /// <summary>
    /// The pure identity-matching contract behind exam `select:` steps: wanted
    /// card IDs resolve against a prompt's candidate list in occurrence order,
    /// each candidate consumed at most once, and an unmatched id is an error
    /// naming both lists (the abort message is a FINDING, so it must be
    /// legible on its own).
    /// </summary>
    public class SelectionAnswerTests
    {
        [Test]
        public void SingleMatch_YieldsTheCandidateIndex()
        {
            Assert.IsTrue(SelectionAnswer.MatchCardIds(
                new[] { "ST1-03" }, new[] { "ST1-02", "ST1-03", "ST1-04" },
                out int[] picks, out string error));
            Assert.IsNull(error);
            CollectionAssert.AreEqual(new[] { 1 }, picks);
        }

        [Test]
        public void Duplicates_ResolveInOccurrenceOrder_EachCandidateConsumedOnce()
        {
            // Two copies wanted, three offered interleaved with another id:
            // the picks must be the FIRST and SECOND occurrences, in order --
            // never the same candidate twice.
            Assert.IsTrue(SelectionAnswer.MatchCardIds(
                new[] { "ST1-03", "ST1-03" },
                new[] { "ST1-03", "ST1-02", "ST1-03", "ST1-03" },
                out int[] picks, out string error));
            Assert.IsNull(error);
            CollectionAssert.AreEqual(new[] { 0, 2 }, picks);
        }

        [Test]
        public void WantedOrder_DrivesPickOrder_NotCandidateOrder()
        {
            Assert.IsTrue(SelectionAnswer.MatchCardIds(
                new[] { "ST1-04", "ST1-02" },
                new[] { "ST1-02", "ST1-03", "ST1-04" },
                out int[] picks, out string error));
            CollectionAssert.AreEqual(new[] { 2, 0 }, picks);
        }

        [Test]
        public void UnmatchedWantedId_IsAnErrorNamingBothLists()
        {
            Assert.IsFalse(SelectionAnswer.MatchCardIds(
                new[] { "ST1-03", "ST1-03" },
                new[] { "ST1-03", "ST1-02" },
                out int[] picks, out string error));
            Assert.AreEqual(0, picks.Length);
            // The message must name the wanted list AND the offered list --
            // it is the finding a human triages, not a stack trace.
            StringAssert.Contains("ST1-03,ST1-03", error);
            StringAssert.Contains("ST1-03,ST1-02", error);
            StringAssert.Contains("pick 1", error);
        }

        [Test]
        public void EmptyWanted_MatchesTriviallyWithEmptyPicks()
        {
            Assert.IsTrue(SelectionAnswer.MatchCardIds(
                new string[0], new[] { "ST1-02" }, out int[] picks, out string error));
            Assert.IsNull(error);
            Assert.AreEqual(0, picks.Length);
        }

        [Test]
        public void NullWanted_MatchesTriviallyWithEmptyPicks()
        {
            Assert.IsTrue(SelectionAnswer.MatchCardIds(
                null, new[] { "ST1-02" }, out int[] picks, out string error));
            Assert.AreEqual(0, picks.Length);
        }

        [Test]
        public void NullCandidateList_IsAnError_NotAnEmptyMatch()
        {
            // A null candidate list means NOT MEASURED. Matching against it
            // would confirm an answer nobody verified.
            Assert.IsFalse(SelectionAnswer.MatchCardIds(
                new[] { "ST1-03" }, null, out int[] picks, out string error));
            StringAssert.Contains("ST1-03", error);
            StringAssert.Contains("NOT MEASURED", error);
        }

        [Test]
        public void Describe_NamesEachPresentPayloadField()
        {
            var step = new HarnessJobStep
            {
                select_card_ids = new[] { "ST1-03" },
                select_value = 3,
                select_has_bool = true,
                select_bool = false,
                select_cancel = true,
            };
            string described = SelectionAnswer.Describe(step);
            StringAssert.Contains("select_card_ids=[ST1-03]", described);
            StringAssert.Contains("select_value=3", described);
            StringAssert.Contains("select_bool=false", described);
            StringAssert.Contains("select_cancel", described);
        }

        [Test]
        public void Describe_NonSelectionStep_SaysSoWithTheActionId()
        {
            var step = new HarnessJobStep { action_id = 12 };
            StringAssert.Contains("action_id=12", SelectionAnswer.Describe(step));
        }
    }
}
