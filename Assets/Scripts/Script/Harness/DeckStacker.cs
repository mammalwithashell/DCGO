using System.Collections.Generic;

namespace Digimon.Harness
{
    /// <summary>
    /// Reorders a freshly-shuffled deck so a named prefix of card IDs sits on
    /// top, in the order named. Everything else keeps its shuffled order.
    /// </summary>
    /// <remarks>
    /// Applied ONLY at the two harness deck-construction short-circuits in
    /// <c>CardObjectController</c> (<c>DeckRecipie</c> and
    /// <c>DigitamaDeckRecipie</c>), never inside
    /// <c>RandomUtility.ShuffledDeckCards</c>.
    ///
    /// That placement is what makes "initial shuffle only" structural. Search
    /// and shuffle effects also route through <c>ShuffledDeckCards</c>; a
    /// stacker living there would silently re-impose the opening order when a
    /// card says "shuffle your deck", and the exam would confidently answer a
    /// question about a game that cannot occur. Mid-game
    /// <c>CardObjectController.Shuffle(Player)</c> never passes through a
    /// harness short-circuit, so there is nothing to exclude and no latch to
    /// get wrong.
    ///
    /// The short-circuits also already resolve the seat
    /// (<c>player == MasterPlayer</c>), which <c>ShuffledDeckCards</c> cannot —
    /// it takes no player argument, so a stacker there would have to guess the
    /// seat from call order.
    /// </remarks>
    public static class DeckStacker
    {
        /// <summary>
        /// Reorder <paramref name="shuffled"/> so <paramref name="stack"/>'s
        /// card IDs lead, in order. Returns a new list; returns the input
        /// unchanged when the stack is null or empty.
        /// Returns null and sets <paramref name="error"/> when the stack names
        /// a card the deck does not hold in sufficient quantity.
        /// </summary>
        public static List<CEntity_Base> Apply(
            List<CEntity_Base> shuffled, string[] stack, out string error)
        {
            error = null;
            if (shuffled == null) { error = "deck is null"; return null; }
            if (stack == null || stack.Length == 0) return shuffled;

            List<CEntity_Base> remainder = new List<CEntity_Base>(shuffled);
            List<CEntity_Base> front = new List<CEntity_Base>(stack.Length);

            foreach (string wanted in stack)
            {
                int at = -1;
                for (int i = 0; i < remainder.Count; i++)
                {
                    if (CardIdOf(remainder[i]) == wanted) { at = i; break; }
                }
                if (at < 0)
                {
                    // Deliberately loud. A stack that silently drops a card
                    // produces a game that looks fine and answers the wrong
                    // question -- the exact failure this whole harness exists
                    // to avoid.
                    error = "stack names '" + wanted +
                            "', which the deck does not contain (or not enough copies of)";
                    return null;
                }
                front.Add(remainder[at]);
                remainder.RemoveAt(at);
            }

            front.AddRange(remainder);
            return front;
        }

        /// <summary>
        /// String-keyed twin of <see cref="Apply"/>, used by the unit tests so
        /// the ordering rules can be exercised without Unity asset loading.
        /// Both delegate to the same algorithm shape; keep them in step.
        /// </summary>
        public static List<string> ApplyIds(List<string> shuffled, string[] stack, out string error)
        {
            error = null;
            if (shuffled == null) { error = "deck is null"; return null; }
            if (stack == null || stack.Length == 0) return shuffled;

            List<string> remainder = new List<string>(shuffled);
            List<string> front = new List<string>(stack.Length);

            foreach (string wanted in stack)
            {
                int at = remainder.IndexOf(wanted);
                if (at < 0)
                {
                    error = "stack names '" + wanted +
                            "', which the deck does not contain (or not enough copies of)";
                    return null;
                }
                front.Add(remainder[at]);
                remainder.RemoveAt(at);
            }

            front.AddRange(remainder);
            return front;
        }

        /// <summary>Card ID as the job spec writes it (e.g. "EX12-035").</summary>
        private static string CardIdOf(CEntity_Base entity)
        {
            return entity == null ? null : entity.CardID;
        }
    }
}
