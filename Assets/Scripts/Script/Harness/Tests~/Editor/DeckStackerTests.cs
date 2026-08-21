using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Digimon.Harness;

namespace Digimon.Harness.Tests
{
    public class DeckStackerTests
    {
        // The stacker only needs an id off each entry, so the tests drive it
        // through a tiny stand-in rather than constructing real CEntity_Base
        // instances (which need Unity asset loading).
        private static List<string> Ids(List<string> deck) => deck;

        [Test]
        public void NullStack_ReturnsInputUnchanged()
        {
            var deck = new List<string> { "A", "B", "C" };
            var result = DeckStacker.ApplyIds(deck, null, out string error);
            Assert.IsNull(error);
            CollectionAssert.AreEqual(new[] { "A", "B", "C" }, result);
        }

        [Test]
        public void EmptyStack_ReturnsInputUnchanged()
        {
            var deck = new List<string> { "A", "B", "C" };
            var result = DeckStacker.ApplyIds(deck, new string[0], out string error);
            Assert.IsNull(error);
            CollectionAssert.AreEqual(new[] { "A", "B", "C" }, result);
        }

        [Test]
        public void PrefixMovesToFront_InStackOrder()
        {
            var deck = new List<string> { "A", "B", "C", "D" };
            var result = DeckStacker.ApplyIds(deck, new[] { "C", "A" }, out string error);
            Assert.IsNull(error);
            CollectionAssert.AreEqual(new[] { "C", "A", "B", "D" }, result);
        }

        [Test]
        public void RemainderKeepsShuffledOrder()
        {
            var deck = new List<string> { "A", "B", "C", "D", "E" };
            var result = DeckStacker.ApplyIds(deck, new[] { "D" }, out string error);
            Assert.IsNull(error);
            CollectionAssert.AreEqual(new[] { "D", "A", "B", "C", "E" }, result);
        }

        [Test]
        public void Duplicates_ConsumeOneCopyEach()
        {
            var deck = new List<string> { "A", "A", "A", "B" };
            var result = DeckStacker.ApplyIds(deck, new[] { "A", "A" }, out string error);
            Assert.IsNull(error);
            CollectionAssert.AreEqual(new[] { "A", "A", "A", "B" }, result);
            Assert.AreEqual(3, result.Count(x => x == "A"));
        }

        [Test]
        public void CardNotInDeck_IsAnError()
        {
            var deck = new List<string> { "A", "B" };
            var result = DeckStacker.ApplyIds(deck, new[] { "Z" }, out string error);
            Assert.IsNull(result);
            StringAssert.Contains("Z", error);
        }

        [Test]
        public void MoreCopiesThanDeckHolds_IsAnError()
        {
            var deck = new List<string> { "A", "B" };
            var result = DeckStacker.ApplyIds(deck, new[] { "A", "A" }, out string error);
            Assert.IsNull(result);
            StringAssert.Contains("A", error);
        }

        [Test]
        public void DeckLengthIsPreserved()
        {
            var deck = new List<string> { "A", "B", "C", "D", "E" };
            var result = DeckStacker.ApplyIds(deck, new[] { "E", "B" }, out string error);
            Assert.IsNull(error);
            Assert.AreEqual(5, result.Count);
        }
    }
}
