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

        // -- MatchOneWithOrdinal -----------------------------------------
        // The single-pick variant used by the MultipleSkills (trigger-order)
        // prompt, whose candidates are a card's stacked TRIGGERS rather than
        // interchangeable copies. Occurrence order must NOT silently decide
        // between two triggers of the same card.

        [Test]
        public void MatchOne_UniqueIdentity_ResolvesWithoutAnOrdinal()
        {
            Assert.IsTrue(SelectionAnswer.MatchOneWithOrdinal(
                "EX12-047", SelectionAnswer.NoOrdinal,
                new[] { "EX12-011", "EX12-047", "EX12-026" },
                out int pick, out string error));
            Assert.IsNull(error);
            Assert.AreEqual(1, pick);
        }

        [Test]
        public void MatchOne_UniqueIdentity_AcceptsTheRedundantOrdinalZero()
        {
            Assert.IsTrue(SelectionAnswer.MatchOneWithOrdinal(
                "EX12-047", 0, new[] { "EX12-011", "EX12-047" },
                out int pick, out string error));
            Assert.AreEqual(1, pick);
        }

        [Test]
        public void MatchOne_UniqueIdentity_RejectsAnOrdinalThatCannotExist()
        {
            // Asking for the 2nd trigger of a card that stacked only one is the
            // author believing something about the position that is not true.
            Assert.IsFalse(SelectionAnswer.MatchOneWithOrdinal(
                "EX12-047", 1, new[] { "EX12-011", "EX12-047" },
                out int pick, out string error));
            Assert.AreEqual(-1, pick);
            StringAssert.Contains("ordinal 1", error);
            StringAssert.Contains("offered exactly once", error);
        }

        [Test]
        public void MatchOne_AmbiguousIdentity_WithoutAnOrdinal_IsAnError_NotTheFirstOccurrence()
        {
            // THE case this method exists for: one deleted carrier stacking an
            // [On Deletion] and an <Ascension> offers its identity twice, and
            // those are different decisions. Taking the first would be a
            // confident wrong answer -- so it must refuse and say how to
            // disambiguate.
            Assert.IsFalse(SelectionAnswer.MatchOneWithOrdinal(
                "EX12-047", SelectionAnswer.NoOrdinal,
                new[] { "EX12-047", "EX12-011", "EX12-047" },
                out int pick, out string error));
            Assert.AreEqual(-1, pick);
            StringAssert.Contains("AMBIGUOUS", error);
            StringAssert.Contains("offered 2 times", error);
            StringAssert.Contains("select_ordinal", error);
            StringAssert.Contains("0..1", error);
            // The offered list is named, because the abort is a FINDING.
            StringAssert.Contains("EX12-047,EX12-011,EX12-047", error);
        }

        [Test]
        public void MatchOne_AmbiguousIdentity_OrdinalPicksAmongThatCardsOwnCandidates()
        {
            // Ordinal 1 is the SECOND EX12-047 (candidate index 2), not
            // candidate index 1 -- the ordinal is scoped to the identity.
            Assert.IsTrue(SelectionAnswer.MatchOneWithOrdinal(
                "EX12-047", 1, new[] { "EX12-047", "EX12-011", "EX12-047" },
                out int pick, out string error));
            Assert.IsNull(error);
            Assert.AreEqual(2, pick);

            Assert.IsTrue(SelectionAnswer.MatchOneWithOrdinal(
                "EX12-047", 0, new[] { "EX12-047", "EX12-011", "EX12-047" },
                out int first, out string _));
            Assert.AreEqual(0, first);
        }

        [Test]
        public void MatchOne_AmbiguousIdentity_OrdinalOutOfRange_IsAnError()
        {
            Assert.IsFalse(SelectionAnswer.MatchOneWithOrdinal(
                "EX12-047", 2, new[] { "EX12-047", "EX12-047" },
                out int pick, out string error));
            Assert.AreEqual(-1, pick);
            StringAssert.Contains("valid ordinals 0..1", error);
        }

        [Test]
        public void MatchOne_NegativeOrdinal_IsAnError()
        {
            Assert.IsFalse(SelectionAnswer.MatchOneWithOrdinal(
                "EX12-047", -1, new[] { "EX12-047", "EX12-047" },
                out int pick, out string error));
            StringAssert.Contains("ordinal -1", error);
        }

        [Test]
        public void MatchOne_UnmatchedIdentity_IsAnErrorNamingTheOfferedList()
        {
            Assert.IsFalse(SelectionAnswer.MatchOneWithOrdinal(
                "EX12-063", SelectionAnswer.NoOrdinal, new[] { "EX12-047", "EX12-011" },
                out int pick, out string error));
            StringAssert.Contains("EX12-063", error);
            StringAssert.Contains("EX12-047,EX12-011", error);
        }

        [Test]
        public void MatchOne_NullCandidateList_IsNotMeasured_NotAMatch()
        {
            Assert.IsFalse(SelectionAnswer.MatchOneWithOrdinal(
                "EX12-047", SelectionAnswer.NoOrdinal, null,
                out int pick, out string error));
            StringAssert.Contains("NOT MEASURED", error);
        }

        [Test]
        public void MatchOne_NoOrdinal_IsTheSameAbsentMarkerTheWireUses()
        {
            // select_ordinal's absent sentinel and NoOrdinal must be the same
            // value, or a step that omitted select_ordinal would read as
            // ordinal int.MinValue and fail every ambiguity check.
            Assert.AreEqual(new HarnessJobStep().select_ordinal, SelectionAnswer.NoOrdinal);
        }

        [Test]
        public void Describe_NamesTheOrdinalSeparatelyFromTheRawValue()
        {
            // The two fields mean different things (a position within one
            // card's triggers vs a raw index into DCGO's list), so an abort
            // message must never blur them.
            string described = SelectionAnswer.Describe(new HarnessJobStep
            {
                select_card_ids = new[] { "EX12-047" },
                select_ordinal = 1,
            });
            StringAssert.Contains("select_ordinal=1", described);
            StringAssert.DoesNotContain("select_value", described);
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
