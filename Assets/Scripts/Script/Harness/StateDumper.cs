using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Digimon.Harness
{
    /// <summary>
    /// Writes one normalized state row per decision boundary, keyed by the
    /// recorder's step index so the sidecar aligns with the recording.
    /// </summary>
    /// <remarks>
    /// State dumping is not optional. Without it a probe reports what was
    /// LEGAL, never what HAPPENED -- and what happened is the question card
    /// authoring asks.
    ///
    /// The projection is deliberately narrow and matches the Rust differ's
    /// expectations exactly: normalize representation, never semantics.
    /// Effective DP is representation (the two engines track modifiers
    /// differently, so a modifier-list diff is pure noise). Whether a Digimon
    /// is suspended is semantics, and must be dumped.
    ///
    /// Security is a COUNT, never contents: the contents are hidden
    /// information, and dumping them would let a differ "confirm" a line whose
    /// legality depended on knowing them.
    ///
    /// <b>Step index.</b> Every row is keyed by
    /// <c>GameRecorder.CurrentStepIndex</c> -- the recorder's OWN counter,
    /// read, never mirrored. A parallel counter here would drift the instant
    /// either side gained a row type that does or does not increment, and a
    /// drifted sidecar makes the differ compare step N of one game against
    /// step N+1 of the other.
    /// </remarks>
    public static class StateDumper
    {
        /// <summary>
        /// Dump each permanent's active keywords. <b>Default OFF.</b>
        /// </summary>
        /// <remarks>
        /// DCGO has no keyword collection -- only ~20 independent bool getters
        /// (<c>Permanent.HasBlocker</c>, <c>HasJamming</c>, ...), each of which
        /// walks the whole field's effect lists. Evaluating all of them per
        /// permanent per step is O(20 x field x effects), so this is opt-in and
        /// meant to be switched on only for scenarios whose clause under exam
        /// is a keyword clause.
        ///
        /// When OFF the <c>keywords</c> key is OMITTED entirely rather than
        /// emitted empty, so the Rust projection reads "not measured" instead
        /// of "no keywords" -- an empty list would make every non-keyword
        /// scenario report a false keyword divergence on every buffed Digimon.
        /// </remarks>
        public static bool DumpKeywords = false;

        private static StreamWriter _writer;

        /// <summary>True while a sidecar is open for the running game.</summary>
        public static bool IsOpen => _writer != null;

        public static void Open(string recordingPath)
        {
            Close();
            if (string.IsNullOrEmpty(recordingPath)) return;
            try
            {
                string sidecar = recordingPath.EndsWith(".jsonl")
                    ? recordingPath.Substring(0, recordingPath.Length - ".jsonl".Length) + ".state.jsonl"
                    : recordingPath + ".state.jsonl";
                _writer = new StreamWriter(sidecar, append: false, new UTF8Encoding(false));
                // Unbuffered, matching GameRecorder: a crashed run must leave
                // everything it observed on disk, since the crash is often the
                // finding.
                _writer.AutoFlush = true;
                Debug.Log("[Harness] state sidecar: " + sidecar);
            }
            catch (System.Exception e)
            {
                Debug.LogError("[Harness] could not open state sidecar: " + e.Message);
                _writer = null;
            }
        }

        public static void Close()
        {
            if (_writer == null) return;
            try { _writer.Flush(); _writer.Dispose(); }
            catch (System.Exception) { }
            _writer = null;
        }

        /// <summary>
        /// Write the current game state under the recorder's step index.
        /// No-op when no sidecar is open (the normal case: a human-started
        /// game, or a harness job whose recording failed to open).
        /// </summary>
        /// <remarks>
        /// Takes no arguments on purpose. Every call site is inside
        /// <c>GameRecorder</c>, which holds no <c>GameContext</c>; resolving
        /// the context here off <c>GManager.instance</c> -- the same read the
        /// recorder's own <c>AppendMemory</c> / <c>AppendBoards</c> helpers do
        /// -- keeps the two describing the same objects at the same instant.
        /// </remarks>
        public static void Dump()
        {
            if (_writer == null) return;

            var recorder = Digimon.Recording.GameRecorder.Instance;
            if (recorder == null) return;

            GameContext ctx = ContextOrNull();
            if (ctx == null) return;

            StringBuilder sb = new StringBuilder(512);
            sb.Append('{');
            sb.Append("\"step\":").Append(recorder.CurrentStepIndex).Append(',');
            sb.Append("\"turn\":").Append(TurnOf()).Append(',');
            sb.Append("\"phase\":\"").Append(Escape(PhaseOf(ctx))).Append("\",");
            sb.Append("\"memory\":").Append(MemoryOf()).Append(',');
            AppendPlayer(sb, "p0", PlayerOf(ctx, 0));
            sb.Append(',');
            AppendPlayer(sb, "p1", PlayerOf(ctx, 1));
            sb.Append('}');

            _writer.WriteLine(sb.ToString());
        }

        // ── Projection ────────────────────────────────────────────────────

        private static void AppendPlayer(StringBuilder sb, string key, Player p)
        {
            sb.Append('"').Append(key).Append("\":{");
            // COUNT ONLY -- see the class remarks. Security contents are
            // hidden information on both sides of the exam.
            sb.Append("\"security\":").Append(p == null ? 0 : p.SecurityCards.Count).Append(',');
            AppendCardIds(sb, "hand", p == null ? null : p.HandCards);
            sb.Append(',');
            AppendCardIds(sb, "trash", p == null ? null : p.TrashCards);
            sb.Append(',');
            sb.Append("\"field\":[");
            if (p != null)
            {
                // GetBattleAreaPermanents, NOT GetFieldPermanents: the latter
                // walks every frame including the BREEDING one, so a hatched
                // egg or a digivolving stack would show up here as though it
                // were on the battle field. The Rust engine's battle_area holds
                // no such thing, so the two lists would describe different
                // zones and manufacture divergences. Same choice, and the same
                // reason, as ActionEncoder.BattleAreaCardIds.
                List<Permanent> field = p.GetBattleAreaPermanents();
                for (int i = 0; i < field.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    AppendPermanent(sb, field[i]);
                }
            }
            sb.Append("]}");
        }

        private static void AppendPermanent(StringBuilder sb, Permanent perm)
        {
            sb.Append('{');
            sb.Append("\"card_id\":\"").Append(Escape(TopCardIdOf(perm))).Append("\",");
            sb.Append("\"dp\":").Append(EffectiveDpOf(perm)).Append(',');
            sb.Append("\"suspended\":").Append(IsSuspended(perm) ? "true" : "false").Append(',');
            AppendCardIds(sb, "sources", SourceCardsOf(perm));
            if (DumpKeywords)
            {
                sb.Append(',');
                AppendStrings(sb, "keywords", ActiveKeywordsOf(perm));
            }
            sb.Append('}');
        }

        private static void AppendCardIds(StringBuilder sb, string key, List<CardSource> cards)
        {
            sb.Append('"').Append(key).Append("\":[");
            if (cards != null)
            {
                for (int i = 0; i < cards.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(Escape(CardIdOf(cards[i]))).Append('"');
                }
            }
            sb.Append(']');
        }

        private static void AppendStrings(StringBuilder sb, string key, List<string> values)
        {
            sb.Append('"').Append(key).Append("\":[");
            if (values != null)
            {
                for (int i = 0; i < values.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(Escape(values[i])).Append('"');
                }
            }
            sb.Append(']');
        }

        // ── Accessors, resolved against the real DCGO types ───────────────

        /// <summary>The live GameContext, or null outside a running game.</summary>
        private static GameContext ContextOrNull()
        {
            if (GManager.instance == null) return null;
            if (GManager.instance.turnStateMachine == null) return null;
            return GManager.instance.turnStateMachine.gameContext;
        }

        /// <summary>
        /// Turn number. Lives on <c>TurnStateMachine</c>, NOT on
        /// <c>GameContext</c> -- there is no <c>ctx.TurnCount</c>.
        /// </summary>
        private static int TurnOf()
        {
            if (GManager.instance == null) return 0;
            if (GManager.instance.turnStateMachine == null) return 0;
            return GManager.instance.turnStateMachine.TurnCount;
        }

        /// <summary><c>GameContext.TurnPhase</c> -- the same read every recorder row's `phase` field uses.</summary>
        private static string PhaseOf(GameContext ctx)
        {
            return ctx == null ? "Unknown" : ctx.TurnPhase.ToString();
        }

        /// <summary>
        /// The memory gauge from the LOCAL client's own perspective
        /// (<c>GManager.instance.You.MemoryForPlayer</c>): positive favors the
        /// recording player, negative favors the opponent.
        /// </summary>
        /// <remarks>
        /// Deliberately the recorder's own convention, not the raw
        /// <c>GameContext.Memory</c> (stored positive-favors-PlayerID-1). The
        /// sidecar and the recording must agree on whose favor a bare number
        /// means, or a reader that guesses wrong silently inverts every
        /// comparison.
        /// </remarks>
        private static int MemoryOf()
        {
            var you = GManager.instance == null ? null : GManager.instance.You;
            return you == null ? 0 : you.MemoryForPlayer;
        }

        private static Player PlayerOf(GameContext ctx, int playerId)
        {
            if (ctx == null) return null;
            if (ctx.You != null && ctx.You.PlayerID == playerId) return ctx.You;
            if (ctx.Opponent != null && ctx.Opponent.PlayerID == playerId) return ctx.Opponent;
            return null;
        }

        private static string TopCardIdOf(Permanent perm)
        {
            if (perm == null) return "";
            return perm.TopCard == null ? "" : perm.TopCard.CardID;
        }

        /// <summary>
        /// DP <b>after</b> modifiers -- the <c>Permanent.DP</c> property, which
        /// walks the field's <c>IChangeDPEffect</c> list.
        /// </summary>
        /// <remarks>
        /// NOT <c>BaseDP</c>: the printed value would make every buffed or
        /// debuffed Digimon read as a divergence. <c>Permanent.DP</c> is also
        /// the value <c>CardController.CompareStats()</c> uses to resolve
        /// battles, so it is the number that actually decides outcomes.
        /// Returns -1 for a permanent with no DP (a Tamer / Option), which is
        /// DCGO's own sentinel from the same getter.
        /// </remarks>
        private static int EffectiveDpOf(Permanent perm)
        {
            return perm == null ? -1 : perm.DP;
        }

        /// <summary><c>Permanent.IsSuspended</c> (the live value; <c>OldIsSuspended</c> is a prior-value snapshot).</summary>
        private static bool IsSuspended(Permanent perm)
        {
            return perm != null && perm.IsSuspended;
        }

        /// <summary>
        /// The stack under the top card (<c>Permanent.DigivolutionCards</c>).
        /// </summary>
        /// <remarks>
        /// DCGO's own ordering, emitted verbatim. The differ must map it
        /// rather than assume it matches the Rust engine's digisource order.
        /// </remarks>
        private static List<CardSource> SourceCardsOf(Permanent perm)
        {
            return perm == null ? null : perm.DigivolutionCards;
        }

        private static string CardIdOf(CardSource card)
        {
            return card == null ? "" : card.CardID;
        }

        /// <summary>
        /// The keyword bools that are currently true on this permanent.
        /// </summary>
        /// <remarks>
        /// There is no <c>Permanent.Keywords</c> collection in DCGO -- each of
        /// these is an independently-computed property that already accounts
        /// for granted / temporary effects, and each walks the whole field's
        /// effect lists. Only reached when <see cref="DumpKeywords"/> is on.
        ///
        /// Names are the DCGO getter suffix verbatim (<c>HasPierce</c> ->
        /// "Pierce"), so every string here maps 1:1 back to the property it
        /// came from. Mapping to the printed English keyword is the Rust
        /// projection's job, where the mapping table can be reviewed once.
        /// </remarks>
        private static List<string> ActiveKeywordsOf(Permanent perm)
        {
            List<string> keywords = new List<string>();
            if (perm == null) return keywords;

            if (perm.HasBlocker) keywords.Add("Blocker");
            if (perm.HasJamming) keywords.Add("Jamming");
            if (perm.HasIceclad) keywords.Add("Iceclad");
            if (perm.HasPierce) keywords.Add("Pierce");
            if (perm.HasReboot) keywords.Add("Reboot");
            if (perm.HasRaid) keywords.Add("Raid");
            if (perm.HasRush) keywords.Add("Rush");
            if (perm.HasRetaliation) keywords.Add("Retaliation");
            if (perm.HasAscension) keywords.Add("Ascension");
            if (perm.HasGuard) keywords.Add("Guard");
            if (perm.HasEngage) keywords.Add("Engage");
            if (perm.HasFortitude) keywords.Add("Fortitude");
            if (perm.HasBlitz) keywords.Add("Blitz");
            if (perm.HasEvade) keywords.Add("Evade");
            if (perm.HasMindLink) keywords.Add("MindLink");
            if (perm.HasBarrier) keywords.Add("Barrier");
            if (perm.HasAlliance) keywords.Add("Alliance");
            if (perm.HasCollision) keywords.Add("Collision");
            if (perm.HasPartition) keywords.Add("Partition");
            if (perm.HasScapegoat) keywords.Add("Scapegoat");

            return keywords;
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
