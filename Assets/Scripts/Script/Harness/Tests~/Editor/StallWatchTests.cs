using NUnit.Framework;
using Digimon.Harness;

namespace Digimon.Harness.Tests
{
    /// <summary>
    /// When a scripted line counts as stalled: a prompt has been showing for
    /// the whole grace, the line's cursor has not moved and the game has made
    /// no progress. Pure, so the timing rule is testable without a game; the
    /// EX7-073 scope-neg job sat on exactly this for 15 minutes with the
    /// heartbeat healthy.
    /// </summary>
    public class StallWatchTests
    {
        [Test]
        public void FiresOnceThePromptHasSatStillForTheGrace()
        {
            var w = new StallWatch(10f);
            Assert.IsFalse(w.Observe(100f, 3, "a", true));
            Assert.IsFalse(w.Observe(109.9f, 3, "a", true));
            Assert.IsTrue(w.Observe(110f, 3, "a", true));
        }

        [Test]
        public void FiresOnlyOnce()
        {
            var w = new StallWatch(10f);
            w.Observe(0f, 3, "a", true);
            Assert.IsTrue(w.Observe(10f, 3, "a", true));
            Assert.IsFalse(w.Observe(20f, 3, "a", true));
        }

        [Test]
        public void GameProgressRestartsTheClock()
        {
            var w = new StallWatch(10f);
            w.Observe(0f, 3, "a", true);
            Assert.IsFalse(w.Observe(9f, 3, "b", true));
            Assert.IsFalse(w.Observe(15f, 3, "b", true));
            Assert.IsTrue(w.Observe(19f, 3, "b", true));
        }

        [Test]
        public void ANewAnswerRestartsTheClock()
        {
            var w = new StallWatch(10f);
            w.Observe(0f, 3, "a", true);
            Assert.IsFalse(w.Observe(9f, 4, "a", true));
            Assert.IsFalse(w.Observe(18f, 4, "a", true));
        }

        [Test]
        public void NoPromptShowingNeverStalls()
        {
            // A long animation chain with no prompt open is not a stall; the
            // job's own timeout_seconds is the net for that.
            var w = new StallWatch(10f);
            w.Observe(0f, 3, "a", false);
            Assert.IsFalse(w.Observe(100f, 3, "a", false));
        }

        [Test]
        public void APromptThatClosesAndReopensRestartsTheClock()
        {
            var w = new StallWatch(10f);
            w.Observe(0f, 3, "a", true);
            w.Observe(5f, 3, "a", false);
            Assert.IsFalse(w.Observe(12f, 3, "a", true));
            Assert.IsTrue(w.Observe(22f, 3, "a", true));
        }

        [Test]
        public void NotArmedBeforeTheLineHasAnsweredAnything()
        {
            // Cursor 0 is game start: Photon connect, scene load, dealing.
            var w = new StallWatch(10f);
            w.Observe(0f, 0, "a", true);
            Assert.IsFalse(w.Observe(100f, 0, "a", true));
        }

        [Test]
        public void AZeroGraceDisablesTheWatch()
        {
            var w = new StallWatch(0f);
            w.Observe(0f, 3, "a", true);
            Assert.IsFalse(w.Observe(1000f, 3, "a", true));
        }
    }
}
