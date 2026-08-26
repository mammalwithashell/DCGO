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
        /// Canonical form of a trigger name, so the two engines' spellings
        /// compare equal: lowercase, with angle brackets and whitespace removed.
        /// </summary>
        /// <remarks>
        /// Must stay behaviourally identical to the Rust side's
        /// normalize_trigger_name (exam/scenario.rs), which filters out
        /// whitespace and the angle brackets and then lowercases. Dropping
        /// whitespace is load-bearing, not cosmetic: DCGO names multi-word
        /// keyword effects WITH a space ("Armor Purge",
        /// CardEffectFactory/KeyWordEffects/ArmorPurge.cs) while the printed
        /// text brackets them.
        /// </remarks>
        public static string NormalizeTriggerName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                if (c == '<' || c == '>' || char.IsWhiteSpace(c)) continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Resolve one wanted card id among a stack of simultaneous triggers by
        /// naming WHICH trigger semantically -- its keyword -- instead of its
        /// position.
        /// </summary>
        /// <param name="wanted">The trigger's source-card id.</param>
        /// <param name="trigger">Keyword name from the step (normalized here).</param>
        /// <param name="candidateIds">Source-card ids, in prompt order.</param>
        /// <param name="candidateTriggers">Each candidate's own effect name
        /// (ICardEffect.EffectName), index-aligned with candidateIds. Null means
        /// NOT MEASURED and is an error, never a licence to fall back to
        /// position.</param>
        /// <remarks>
        /// Filters by trigger FIRST, then by identity, mirroring the Rust
        /// match_one_branch. Zero matches is a FINDING about the stack's SHAPE
        /// -- our engine offered a branch DCGO did not stage, or named one that
        /// is not a keyword effect here -- so it refuses loudly rather than
        /// falling through to the first candidate. More than one match means the
        /// same card staged the same keyword twice, which only select_ordinal
        /// can separate.
        /// </remarks>
        public static bool MatchOneWithTrigger(string wanted, string trigger,
                                               IList<string> candidateIds,
                                               IList<string> candidateTriggers,
                                               out int pick, out string error)
        {
            pick = -1;
            error = null;

            string want = wanted ?? "";
            string wantTrigger = NormalizeTriggerName(trigger);

            if (candidateIds == null || candidateTriggers == null)
            {
                error = "wanted card '" + want + "' trigger '" + wantTrigger +
                        "' but the prompt's candidate list could not be computed " +
                        "(NOT MEASURED), so triggers cannot be matched";
                return false;
            }

            if (wantTrigger.Length == 0)
            {
                error = "select_trigger is empty after normalization -- name the " +
                        "keyword, e.g. 'Ascension'";
                return false;
            }

            List<int> matches = new List<int>();
            int upper = candidateIds.Count < candidateTriggers.Count
                ? candidateIds.Count : candidateTriggers.Count;
            for (int c = 0; c < upper; c++)
            {
                if (!string.Equals(want, candidateIds[c] ?? "", StringComparison.Ordinal)) continue;
                if (NormalizeTriggerName(candidateTriggers[c]) != wantTrigger) continue;
                matches.Add(c);
            }

            if (matches.Count == 0)
            {
                error = "wanted card '" + want + "' with trigger '" + wantTrigger +
                        "' is not among the offered branches [" +
                        DescribeBranches(candidateIds, candidateTriggers) +
                        "] -- the two engines disagree about what this stack contains";
                return false;
            }

            if (matches.Count > 1)
            {
                error = "wanted card '" + want + "' with trigger '" + wantTrigger +
                        "' is offered " + matches.Count + " times by [" +
                        DescribeBranches(candidateIds, candidateTriggers) +
                        "] -- one card staged the same keyword twice, so only " +
                        "select_ordinal can separate them";
                return false;
            }

            pick = matches[0];
            return true;
        }

        /// <summary>
        /// Resolve one wanted card id among a stack of simultaneous triggers by
        /// naming the branch to EXCLUDE -- the complement of
        /// <see cref="MatchOneWithTrigger"/>.
        /// </summary>
        /// <remarks>
        /// For a wanted branch with no keyword of its own this is the only handle
        /// either engine can compute without a registry of what counts as a
        /// keyword. Mirrors the Rust `match_one_branch` exclusion arm: take the
        /// branches this card offers, drop the ones whose name matches, and
        /// require EXACTLY ONE survivor.
        ///
        /// Both failure modes refuse rather than guess, for the same reason the
        /// positive form does. Zero survivors means the stack is smaller than the
        /// author believed, or every branch IS the excluded keyword. More than one
        /// means the exclusion did not isolate a branch, so it has run out of
        /// resolving power exactly as a repeated keyword does.
        /// </remarks>
        public static bool MatchOneExcludingTrigger(string wanted, string excludedTrigger,
                                                    IList<string> candidateIds,
                                                    IList<string> candidateTriggers,
                                                    out int pick, out string error)
        {
            pick = -1;
            error = null;

            string want = wanted ?? "";
            string excluded = NormalizeTriggerName(excludedTrigger);

            if (candidateIds == null || candidateTriggers == null)
            {
                error = "wanted card '" + want + "' excluding trigger '" + excluded +
                        "' but the prompt's candidate list could not be computed " +
                        "(NOT MEASURED), so branches cannot be matched";
                return false;
            }

            if (excluded.Length == 0)
            {
                error = "select_trigger_not is empty after normalization -- name the " +
                        "keyword to exclude, e.g. 'Ascension'";
                return false;
            }

            List<int> mine = new List<int>();
            int upper = candidateIds.Count < candidateTriggers.Count
                ? candidateIds.Count : candidateTriggers.Count;
            for (int c = 0; c < upper; c++)
            {
                if (string.Equals(want, candidateIds[c] ?? "", StringComparison.Ordinal))
                {
                    mine.Add(c);
                }
            }

            if (mine.Count == 0)
            {
                error = "wanted card '" + want + "' is not among the offered branches [" +
                        DescribeBranches(candidateIds, candidateTriggers) + "]";
                return false;
            }

            List<int> survivors = new List<int>();
            foreach (int i in mine)
            {
                if (NormalizeTriggerName(candidateTriggers[i]) != excluded) survivors.Add(i);
            }

            if (survivors.Count == 0)
            {
                error = "select_trigger_not '" + excluded + "' excluded every branch card '" +
                        want + "' offers, leaving nothing to pick [" +
                        DescribeBranches(candidateIds, candidateTriggers) + "]";
                return false;
            }

            if (survivors.Count > 1)
            {
                error = "select_trigger_not '" + excluded + "' leaves " + survivors.Count +
                        " branches of card '" + want + "', so it does not say which [" +
                        DescribeBranches(candidateIds, candidateTriggers) + "]";
                return false;
            }

            pick = survivors[0];
            return true;
        }

        /// <summary>Per-branch "id 'EffectName'", for the abort messages above.</summary>
        static string DescribeBranches(IList<string> ids, IList<string> triggers)
        {
            if (ids == null) return "";
            List<string> parts = new List<string>();
            for (int i = 0; i < ids.Count; i++)
            {
                string t = (triggers != null && i < triggers.Count) ? triggers[i] : null;
                parts.Add((ids[i] ?? "") + (string.IsNullOrEmpty(t) ? "" : " '" + t + "'"));
            }
            return string.Join(" | ", parts.ToArray());
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
