using System;
using System.Collections.Generic;

namespace Digimon.Harness
{
    /// <summary>
    /// Pure identity-matching logic for scripted selection steps.
    /// </summary>
    /// <remarks>
    /// The wire carries card IDENTITIES (printed card IDs), never
    /// engine-internal indices: our engine's selection ids are our encodings,
    /// and DCGO's RPCs want DCGO-internal values (ActiveCardList CardIndex,
    /// frame ids). Each engine resolves the identities against its OWN
    /// candidate list; this class is DCGO's half of that resolution.
    ///
    /// Pure by design -- no MonoBehaviour, no Unity types, no statics with
    /// state -- so the matching rules are unit-testable without a Unity
    /// runtime, the same reasoning that shaped <see cref="ScriptedLine"/>.
    ///
    /// KNOWN LIMITATION: in <see cref="MatchCardIds"/>, two same-identity
    /// candidates are distinguished only by occurrence order. That is a
    /// documented property of the exam wire, not an accident -- it is right
    /// where the duplicates are interchangeable copies of a card.
    /// <see cref="MatchOneWithOrdinal"/> is the variant for prompts where they
    /// are NOT (a card's several stacked triggers); it refuses to guess and
    /// requires an explicit ordinal instead.
    /// </remarks>
    public static class SelectionAnswer
    {
        /// <summary>
        /// Resolve wanted card IDs against an offered candidate list, in
        /// occurrence order, consuming each candidate at most once.
        /// </summary>
        /// <param name="wanted">Card IDs the script asks for, in pick order.
        /// Null or empty means zero picks and matches trivially.</param>
        /// <param name="candidateIds">Card IDs the prompt offers, in the
        /// prompt's own order. Null means the candidate list could not be
        /// computed -- matching anything against it is an error, because an
        /// unverifiable match is a wrong answer waiting to happen.</param>
        /// <param name="picks">Indices into <paramref name="candidateIds"/>,
        /// one per wanted id, in the wanted order.</param>
        /// <param name="error">On failure, a message naming BOTH lists. The
        /// abort message this feeds is a FINDING ("DCGO does not offer what
        /// our engine offered"), so it has to be legible on its own.</param>
        public static bool MatchCardIds(IList<string> wanted, IList<string> candidateIds,
                                        out int[] picks, out string error)
        {
            picks = new int[0];
            error = null;

            if (wanted == null || wanted.Count == 0) return true;

            if (candidateIds == null)
            {
                error = "wanted cards [" + Join(wanted) + "] but the prompt's candidate " +
                        "list could not be computed (NOT MEASURED), so identities cannot be matched";
                return false;
            }

            bool[] consumed = new bool[candidateIds.Count];
            int[] result = new int[wanted.Count];

            for (int w = 0; w < wanted.Count; w++)
            {
                string want = wanted[w] ?? "";
                int found = -1;
                for (int c = 0; c < candidateIds.Count; c++)
                {
                    if (consumed[c]) continue;
                    if (string.Equals(want, candidateIds[c] ?? "", StringComparison.Ordinal))
                    {
                        found = c;
                        break;
                    }
                }

                if (found < 0)
                {
                    error = "wanted card '" + want + "' (pick " + w + " of [" + Join(wanted) +
                            "]) is not among the offered candidates [" + Join(candidateIds) + "]";
                    return false;
                }

                consumed[found] = true;
                result[w] = found;
            }

            picks = result;
            return true;
        }

        /// <summary>Sentinel for "this step carried no ordinal".</summary>
        /// <remarks>
        /// The same <see cref="int.MinValue"/> absent-marker
        /// <see cref="HarnessJobStep.select_ordinal"/> uses, named here so the
        /// ordinal overload reads as an ordinal rather than as a raw index.
        /// </remarks>
        public const int NoOrdinal = int.MinValue;

        /// <summary>
        /// Resolve exactly ONE wanted card ID against an offered candidate
        /// list, disambiguating same-identity candidates by an explicit
        /// ordinal instead of by occurrence order.
        /// </summary>
        /// <remarks>
        /// <see cref="MatchCardIds"/> resolves duplicates positionally, which
        /// is right for "pick 2 Koromon from a hand" -- the copies are
        /// interchangeable. It is WRONG for a single-pick prompt whose
        /// candidates are a card's several stacked TRIGGERS (an [On Deletion]
        /// and an &lt;Ascension&gt; on the same deleted carrier both offer that
        /// carrier's identity): there the duplicates are different decisions,
        /// so silently taking the first would be a confident wrong answer of
        /// exactly the kind the harness exists to prevent.
        ///
        /// So: one match resolves with no ordinal; several matches REQUIRE one
        /// and error without it, naming every candidate.
        /// </remarks>
        /// <param name="wanted">The card ID the script asks for.</param>
        /// <param name="ordinal">0-based position AMONG the candidates
        /// carrying <paramref name="wanted"/>, or <see cref="NoOrdinal"/> when
        /// the step carried none.</param>
        /// <param name="candidateIds">Card IDs the prompt offers, in the
        /// prompt's own order. Null means the candidate list could not be
        /// computed -- NOT MEASURED, and an unverifiable match is a wrong
        /// answer waiting to happen.</param>
        /// <param name="pick">Index into <paramref name="candidateIds"/>.</param>
        /// <param name="error">On failure, a message naming the wanted id, the
        /// ordinal, and the offered list -- it feeds an abort that a human
        /// triages as a FINDING, so it has to be legible on its own.</param>
        public static bool MatchOneWithOrdinal(string wanted, int ordinal,
                                               IList<string> candidateIds,
                                               out int pick, out string error)
        {
            pick = -1;
            error = null;

            string want = wanted ?? "";

            if (candidateIds == null)
            {
                error = "wanted card '" + want + "' but the prompt's candidate list could not " +
                        "be computed (NOT MEASURED), so identities cannot be matched";
                return false;
            }

            List<int> matches = new List<int>();
            for (int c = 0; c < candidateIds.Count; c++)
            {
                if (string.Equals(want, candidateIds[c] ?? "", StringComparison.Ordinal))
                {
                    matches.Add(c);
                }
            }

            if (matches.Count == 0)
            {
                error = "wanted card '" + want + "' is not among the offered candidates [" +
                        Join(candidateIds) + "]";
                return false;
            }

            if (matches.Count == 1)
            {
                if (ordinal != NoOrdinal && ordinal != 0)
                {
                    error = "wanted card '" + want + "' with ordinal " + ordinal +
                            ", but it is offered exactly once by [" + Join(candidateIds) +
                            "] (only ordinal 0 exists)";
                    return false;
                }
                pick = matches[0];
                return true;
            }

            if (ordinal == NoOrdinal)
            {
                error = "wanted card '" + want + "' is AMBIGUOUS: it is offered " + matches.Count +
                        " times by [" + Join(candidateIds) + "]. Add select_ordinal, the 0-based " +
                        "position among that card's own candidates (0.." + (matches.Count - 1) + ")";
                return false;
            }

            if (ordinal < 0 || ordinal >= matches.Count)
            {
                error = "wanted card '" + want + "' with ordinal " + ordinal +
                        ", but it is offered " + matches.Count + " times by [" +
                        Join(candidateIds) + "] (valid ordinals 0.." + (matches.Count - 1) + ")";
                return false;
            }

            pick = matches[ordinal];
            return true;
        }

        /// <summary>
        /// One-line description of a step's selection payload, for abort
        /// messages. A hook that receives the WRONG payload shape for its
        /// prompt kind aborts with this, so the author can see exactly what
        /// the step carried.
        /// </summary>
        public static string Describe(HarnessJobStep step)
        {
            if (step == null) return "<null step>";
            if (!step.IsSelection)
            {
                return "no selection payload (action_id=" + step.action_id + ")";
            }

            List<string> parts = new List<string>();
            if (step.select_card_ids != null && step.select_card_ids.Length > 0)
            {
                parts.Add("select_card_ids=[" + Join(step.select_card_ids) + "]");
            }
            if (step.select_value != int.MinValue)
            {
                parts.Add("select_value=" + step.select_value);
            }
            if (step.select_ordinal != int.MinValue)
            {
                parts.Add("select_ordinal=" + step.select_ordinal);
            }
            if (step.select_has_bool)
            {
                parts.Add("select_bool=" + (step.select_bool ? "true" : "false"));
            }
            if (step.select_cancel)
            {
                parts.Add("select_cancel=true");
            }
            return string.Join(", ", parts.ToArray());
        }

        private static string Join(IList<string> items)
        {
            if (items == null) return "";
            string[] parts = new string[items.Count];
            for (int i = 0; i < items.Count; i++) parts[i] = items[i] ?? "";
            return string.Join(",", parts);
        }
    }
}
