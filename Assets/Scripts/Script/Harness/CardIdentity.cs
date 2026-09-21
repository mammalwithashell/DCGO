namespace Digimon.Harness
{
    /// <summary>
    /// The one place a <see cref="CardSource"/> is turned into the stable id the
    /// exam harness compares against our Rust engine.
    /// </summary>
    /// <remarks>
    /// A TOKEN has no printed card number, so <c>CardSource.CardID</c> is the
    /// empty string for one. Every site that fed a raw <c>CardID ?? ""</c> into
    /// the harness therefore described every token -- Petrification, Paishu,
    /// Hinukamuy -- as the SAME nameless card. Two concrete failures came from
    /// that: the state sidecar reported `card_id: ""` where our engine reports
    /// `TOKEN_PETRIFICATION`, so any scenario whose end state held a token
    /// diverged on representation alone; and a scripted `select:` naming a token
    /// could not match, because the offered candidate list read `[,]`.
    ///
    /// Tokens DO carry identity here: <c>CEntity_Base.CardName_ENG</c> is set at
    /// construction in <c>ContinuousController</c> ("Petrification", "Paishu",
    /// "Hinukamuy"), so the id is DERIVED rather than invented, matches the Rust
    /// side's <c>TOKEN_&lt;NAME&gt;</c> convention
    /// (<c>code/digimon-engine/src/cards/tokens/mod.rs</c>), and a NEW token gets
    /// a correct id with no change here.
    ///
    /// Deck lists (<c>TurnStateMachine</c>) deliberately do NOT use this: a deck
    /// can never contain a token, so a raw <c>CardID</c> is right there.
    /// </remarks>
    public static class CardIdentity
    {
        /// <summary>
        /// <paramref name="card"/>'s <c>CardID</c>, or <c>TOKEN_&lt;NAME&gt;</c>
        /// when it is a token. Empty string for null, and for a token with no
        /// name -- an unnamed token stays UNIDENTIFIED rather than colliding
        /// with every other unnamed token under a bare "TOKEN_".
        /// </summary>
        public static string Of(CardSource card)
        {
            if (card == null) return "";
            string id = card.CardID;
            if (!string.IsNullOrEmpty(id)) return id;
            if (!card.IsToken) return "";
            string name = card.BaseENGCardNameFromEntity;
            if (string.IsNullOrEmpty(name)) return "";
            return "TOKEN_" + name.ToUpperInvariant().Replace(' ', '_');
        }
    }
}
