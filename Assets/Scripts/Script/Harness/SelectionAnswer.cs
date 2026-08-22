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
    /// KNOWN LIMITATION: two same-identity candidates are distinguished only
    /// by occurrence order. That is a documented property of the exam wire,
    /// not an accident.
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
