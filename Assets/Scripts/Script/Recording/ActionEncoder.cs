using System.Collections.Generic;
using System.Reflection;

namespace Digimon.Recording
{
    /// <summary>
    /// Encodes DCGO decisions into 2192-action-space IDs (see
    /// <see cref="ActionSpace"/>, generated from
    /// <c>code/digimon-engine/src/action/space.rs</c>).
    /// </summary>
    /// <remarks>
    /// Encoding is partial in Phase 1:
    ///
    /// <list type="bullet">
    ///   <item>Mulligan: fully encoded (action IDs 0/1).</item>
    ///   <item>Main-phase actions: fully encoded for PassAction, AttackPermanentAction,
    ///         ActivatePermanentAction, PlayCardAction (base play; digivolution
    ///         sources are decomposed into separate action IDs via
    ///         <see cref="DecomposePlayCardExtras"/>).</item>
    ///   <item>ActivateCardAction: encoded as <c>HAND_EFFECT_START + cardIdx</c> when
    ///         <c>skillIdx == 0</c>; non-zero skill indices currently fall back to
    ///         <c>encoder_failure</c> rows pending an action-space extension. Most
    ///         hand cards expose a single [Hand][Main] effect so this is rare.</item>
    ///   <item>CheatAction: not encoded; emits encoder_failure. Cheats are debug-only.</item>
    ///   <item>Selections (int and bool): emit encoder_failure rows for every prompt
    ///         in Phase 1. The full per-prompt mapping requires plumbing prompt
    ///         identity from each Select*Effect into UserSelectionManager —
    ///         deferred to a follow-up. The raw value is captured so replays can
    ///         identify the divergence point and the BC pipeline can backfill
    ///         encoded action IDs once the per-prompt mapping lands.</item>
    /// </list>
    ///
    /// All encoder functions are static and pure; no DCGO singletons are read
    /// except through the explicit <c>Player</c> argument. This keeps the
    /// encoder unit-testable in EditMode without spinning up a Unity scene.
    /// </remarks>
    public static class ActionEncoder
    {
        /// <summary>
        /// Result of encoding a decision. Either a valid 2192-space action ID
        /// or a failure with debug context.
        /// </summary>
        public struct Encoded
        {
            public bool IsFailure;
            public ushort ActionId;
            public string FailureReason;
            public string RawDebugValue;

            public static Encoded Ok(ushort id) =>
                new Encoded { IsFailure = false, ActionId = id };

            public static Encoded Fail(string reason, string rawDebug = null) =>
                new Encoded
                {
                    IsFailure = true,
                    FailureReason = reason,
                    RawDebugValue = rawDebug ?? "",
                };
        }

        // ── Mulligan ──────────────────────────────────────────────────────

        /// <summary>
        /// Encode a mulligan decision. Action IDs 0 (keep) and 1 (redraw) are
        /// the mulligan-phase IDs in our action space.
        /// </summary>
        public static Encoded EncodeMulligan(bool redrew) =>
            Encoded.Ok((ushort)(redrew ? 1 : 0));

        // ── Main-phase actions ────────────────────────────────────────────

        /// <summary>
        /// Encode a <see cref="MainPhaseAction"/> into a 2192-space ID.
        /// </summary>
        /// <param name="actorPlayerId">0 or 1.</param>
        /// <param name="action">The action being queued. Must be one of the
        /// six registered subclasses (see <c>GamePacketRegistration</c>).</param>
        /// <param name="actor">The acting player. Used to translate DCGO's
        /// PermanentIndex (an index into <c>GetFieldPermanents()</c>, which is
        /// the compact list of non-empty field slots) into our action space's
        /// slot index (the stable frame ID, 0..13).</param>
        public static Encoded EncodeMainPhaseAction(int actorPlayerId,
                                                    MainPhaseAction action,
                                                    Player actor)
        {
            switch (action)
            {
                case PlayCardAction play:
                    return EncodePlayCard(play);
                case AttackPermanentAction atk:
                    return EncodeAttack(atk, actor);
                case ActivatePermanentAction actPerm:
                    return EncodeActivatePermanent(actPerm, actor);
                case ActivateCardAction actCard:
                    return EncodeActivateCard(actCard);
                case PassAction _:
                    return Encoded.Ok(ActionSpace.PASS);
                case CheatAction cheat:
                    // CheatType is the CheatAction.Type enum, not an int —
                    // read as object to get a sensible string representation.
                    return Encoded.Fail("cheat_action_unsupported",
                                        rawDebug: $"cheat type {ReadFieldAsObject(cheat, "CheatType")}");
                default:
                    return Encoded.Fail("unknown_main_phase_action_type",
                                        rawDebug: action?.GetType().FullName ?? "null");
            }
        }

        private static Encoded EncodePlayCard(PlayCardAction play)
        {
            int cardIndex = ReadField<int>(play, "CardIndex");
            if (cardIndex < 0 || cardIndex >= ActionSpace.PLAY_HAND_END)
            {
                return Encoded.Fail("play_card_hand_index_out_of_range",
                                    rawDebug: cardIndex.ToString());
            }
            // PLAY_HAND_START is 0, so the raw cardIndex IS the action ID.
            return Encoded.Ok((ushort)(ActionSpace.PLAY_HAND_START + cardIndex));
        }

        private static Encoded EncodeAttack(AttackPermanentAction atk, Player actor)
        {
            int attackerCompactIdx = ReadField<int>(atk, "PermanentIndex");
            int targetCompactIdx   = ReadField<int>(atk, "AttackTargetPermanentIndex");

            // Translate DCGO compact index → frame ID. The Rust action space
            // is keyed on frame slot (0..13), not on the GetFieldPermanents
            // list position. AttackTargetPermanentIndex == -1 means "attack
            // security" — our SECURITY_TARGET (14) covers that.
            int attackerFrame = CompactIndexToFrameId(actor, attackerCompactIdx);
            if (attackerFrame < 0)
                return Encoded.Fail("attack_attacker_frame_lookup_failed",
                                    rawDebug: attackerCompactIdx.ToString());

            int targetFrame;
            if (targetCompactIdx < 0)
            {
                targetFrame = ActionSpace.SECURITY_TARGET; // 14
            }
            else
            {
                // Attack target is one of the opponent's permanents (or own,
                // in the rare case of self-targeted attacks). DCGO encodes
                // target relative to the OPPONENT'S compact list — we need
                // the enemy's frame.
                var enemy = actor.Enemy;
                targetFrame = CompactIndexToFrameId(enemy, targetCompactIdx);
                if (targetFrame < 0)
                    return Encoded.Fail("attack_target_frame_lookup_failed",
                                        rawDebug: targetCompactIdx.ToString());
            }

            if (attackerFrame >= ActionSpace.MAX_FIELD_SLOTS ||
                targetFrame >= ActionSpace.TARGETS_PER_ATTACKER)
            {
                return Encoded.Fail("attack_frame_out_of_action_space_range",
                                    rawDebug: $"attacker={attackerFrame} target={targetFrame}");
            }

            return Encoded.Ok(ActionSpace.EncodeAttack(attackerFrame, targetFrame));
        }

        private static Encoded EncodeActivatePermanent(ActivatePermanentAction act, Player actor)
        {
            int permCompactIdx = ReadField<int>(act, "PermanentIndex");
            int skillIdx       = ReadField<int>(act, "SkillIndex");

            int frame = CompactIndexToFrameId(actor, permCompactIdx);
            if (frame < 0)
                return Encoded.Fail("activate_permanent_frame_lookup_failed",
                                    rawDebug: permCompactIdx.ToString());

            if (frame >= ActionSpace.MAX_FIELD_SLOTS ||
                skillIdx < 0 || skillIdx >= ActionSpace.EFFECTS_PER_PERMANENT)
            {
                return Encoded.Fail("activate_permanent_out_of_range",
                                    rawDebug: $"frame={frame} skill={skillIdx}");
            }

            return Encoded.Ok(ActionSpace.EncodeFieldEffect(frame, skillIdx));
        }

        private static Encoded EncodeActivateCard(ActivateCardAction act)
        {
            int cardIdx  = ReadField<int>(act, "CardIndex");
            int skillIdx = ReadField<int>(act, "SkillIndex");

            // Our HAND_EFFECT range (30..60) is one slot per hand index — no
            // per-skill sub-slot. Most hand cards expose a single [Hand][Main]
            // effect, so skill_idx == 0 is overwhelmingly the common case.
            // Non-zero skill indices on hand cards exist (e.g., dual-effect
            // option cards) — we surface them as encoder failures so the BC
            // pipeline doesn't silently mis-label them.
            if (skillIdx != 0)
            {
                return Encoded.Fail("activate_card_nonzero_skill_index",
                                    rawDebug: $"card={cardIdx} skill={skillIdx}");
            }
            if (cardIdx < 0 || cardIdx >= ActionSpace.HAND_MAIN_LIMIT)
            {
                return Encoded.Fail("activate_card_hand_index_out_of_range",
                                    rawDebug: cardIdx.ToString());
            }
            return Encoded.Ok((ushort)(ActionSpace.HAND_EFFECT_START + cardIdx));
        }

        /// <summary>
        /// Decompose the digivolution-source pickers baked into a
        /// PlayCardAction into separate source-selection action IDs.
        ///
        /// DCGO packs jogress (DNA) sources, burst-tamer source, and
        /// app-fusion sources into one PlayCardAction packet. In the Rust
        /// action space those are surfaced as N subsequent SOURCE_SELECT
        /// actions (or DNA_DIGIVOLVE / breeding-source-select rows for the
        /// specific variants). We emit one extra row per packed source so
        /// the replay sees the same N+1 action stream the engine expects.
        /// </summary>
        /// <remarks>
        /// Returns an empty enumerable for non-PlayCard actions, and for
        /// PlayCard actions with no digivolution sources.
        ///
        /// Encoding is best-effort: when an extra source can't be mapped
        /// cleanly into our action space, we omit it with a debug log.
        /// The downstream replay harness will then see a legitimate action
        /// stream up to the point of omission and surface a parity failure
        /// at the next mismatching step — that's the right diagnostic
        /// posture (don't mask the gap with synthetic encoder_failure rows).
        /// </remarks>
        public static IEnumerable<Encoded> DecomposePlayCardExtras(MainPhaseAction action,
                                                                    int actorPlayerId)
        {
            if (!(action is PlayCardAction play)) yield break;

            // Field name lookups match the PlayCardAction class definition
            // in DCGO/Assets/Scripts/Script/MainPhaseAction/PlayCardAction.cs:
            //   int CardIndex
            //   int TargetFrameID
            //   int[] JogressEvoRootsFrameIDs
            //   int BurstTamerFrameID
            //   int[] AppFusionFrameIDs
            var jogress = ReadField<int[]>(play, "JogressEvoRootsFrameIDs");
            int burst   = ReadField<int>(play, "BurstTamerFrameID");
            var appfuse = ReadField<int[]>(play, "AppFusionFrameIDs");

            // Jogress / DNA digivolve roots — two field permanents tribute
            // into one. Encoded as DNA_DIGIVOLVE range entries; full per-
            // root mapping requires more action-space surface than we have
            // here (DNA_DIGIVOLVE is 63..93, only 30 slots, indexed by hand
            // index not frame). For Phase 1 we surface jogress extras as
            // explicit encoder_failure rows so the replay sees the
            // divergence rather than silently dropping picks.
            if (jogress != null)
            {
                for (int i = 0; i < jogress.Length; i++)
                {
                    yield return Encoded.Fail(
                        "play_card_jogress_root_pending_encoding",
                        rawDebug: $"slot{i}_frame={jogress[i]}");
                }
            }

            // Burst-tamer source — a tamer card the player is burst-evolving
            // off. Conceptually a source pick on the played card; in our
            // space this would be encoded as a SOURCE_SELECT row. The frame
            // here is the tamer's field frame, which is what SOURCE_SELECT
            // expects. The source_index for the tamer-as-source is always 0
            // (it's the entire tamer permanent, not a digivolution stack
            // position).
            if (burst >= 0)
            {
                if (burst < ActionSpace.MAX_FIELD_SLOTS)
                    yield return Encoded.Ok(ActionSpace.EncodeSourceSelect(burst, 0));
                else
                    yield return Encoded.Fail("play_card_burst_frame_out_of_range",
                                              rawDebug: burst.ToString());
            }

            // App-fusion sources — variable-length list of (frame, slot)
            // pairs in DCGO. Pair layout from PlayCardAction.Serialize:
            //   AppFusionFrameIDs = [permFrame0, linkedSlot0, permFrame1, linkedSlot1, ...]
            // (Inferred from PlayCardAction's Serialize loop pushing them
            // pairwise.) Each pair maps to one SOURCE_SELECT row.
            if (appfuse != null)
            {
                for (int i = 0; i + 1 < appfuse.Length; i += 2)
                {
                    int frame = appfuse[i];
                    int slot  = appfuse[i + 1];
                    if (frame >= 0 && frame < ActionSpace.MAX_FIELD_SLOTS &&
                        slot >= 0 && slot < ActionSpace.SOURCES_PER_FIELD)
                    {
                        yield return Encoded.Ok(ActionSpace.EncodeSourceSelect(frame, slot));
                    }
                    else
                    {
                        yield return Encoded.Fail("play_card_appfusion_pair_out_of_range",
                                                   rawDebug: $"frame={frame} slot={slot}");
                    }
                }
            }
        }

        // ── Selections ────────────────────────────────────────────────────

        /// <summary>
        /// Encode an int-valued selection response. Phase 1 fallback: emits
        /// encoder_failure with the raw value.
        ///
        /// To properly encode selections we need to know which Select*Effect
        /// is currently prompting the player. The 15 prompt types each map
        /// to a different range in our action space:
        /// <list type="bullet">
        ///   <item>SelectPermanentEffect      → ATTACK_START + slot * 15 + target slot, or selection_id depending on prompt subtype</item>
        ///   <item>SelectHandEffect           → 30..59 hand index</item>
        ///   <item>SelectCardEffect           → context-dependent (revealed / security / hand)</item>
        ///   <item>SelectCountEffect          → custom integer (no direct action-space mapping yet)</item>
        ///   <item>SelectAttackEffect         → ATTACK_START range</item>
        ///   <item>SelectAppFusionEffect      → SOURCE_SELECT range</item>
        ///   <item>SelectJogressEffect        → DNA_DIGIVOLVE range</item>
        ///   <item>SelectBurstDigivolutionEffect → DIGIVOLVE range</item>
        ///   <item>SelectDigiXrosClass        → custom</item>
        ///   <item>SelectAssemblyClass        → custom</item>
        ///   <item>SelectDNACondition         → custom</item>
        ///   <item>OptionalSkill (yes/no)     → 0/1 or PASS=62 decline</item>
        ///   <item>SelectCommand              → custom UI selection (e.g., attack vs activate vs digivolve)</item>
        ///   <item>SelectCardPanel            → context-dependent</item>
        ///   <item>SelectCommandPanel         → context-dependent</item>
        /// </list>
        ///
        /// Resolving prompt identity requires either (a) reading the active
        /// UI state from <c>GManager.instance.selectCommandPanel</c>, or
        /// (b) plumbing a prompt-kind string through SetIntSelection /
        /// SetBoolSelection signatures. Both are non-trivial; deferred.
        ///
        /// In the meantime, the encoder_failure rows carry the raw value so
        /// the replay harness can identify the divergence point and the
        /// downstream BC pipeline can backfill encodings once prompt
        /// identity is plumbed.
        /// </summary>
        public static Encoded EncodeSelectionInt(int actorPlayerId, int value, string phaseName)
        {
            return Encoded.Fail("selection_prompt_kind_unknown",
                                rawDebug: $"int_value={value} phase={phaseName}");
        }

        /// <summary>
        /// Encode a bool-valued selection response. See
        /// <see cref="EncodeSelectionInt"/> for context.
        ///
        /// Bool prompts are typically optional-trigger yes/no decisions.
        /// In our action space those map to either a specific accept ID or
        /// PASS=62 for decline, depending on the prompt subtype.
        /// </summary>
        public static Encoded EncodeSelectionBool(int actorPlayerId, bool value, string phaseName)
        {
            return Encoded.Fail("selection_prompt_kind_unknown",
                                rawDebug: $"bool_value={value} phase={phaseName}");
        }

        // ── Helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Translate DCGO's <c>PermanentIndex</c> (index into the compact
        /// non-empty list returned by <c>Player.GetFieldPermanents()</c>) into
        /// our action-space slot index (the stable FrameID, 0..13).
        /// </summary>
        /// <returns>Frame ID in [0, MAX_FIELD_SLOTS), or -1 if lookup fails.</returns>
        private static int CompactIndexToFrameId(Player player, int compactIndex)
        {
            if (player == null) return -1;
            var perms = player.GetFieldPermanents();
            if (compactIndex < 0 || compactIndex >= perms.Count) return -1;

            var perm = perms[compactIndex];
            // Permanent.PermanentFrame is the FieldCardFrame this permanent
            // currently sits on; .FrameID is the stable slot index.
            // (See DCGO/Assets/Scripts/Script/Permanent.cs and FieldCardFrame.)
            try
            {
                var frame = perm.PermanentFrame;
                return frame != null ? frame.FrameID : -1;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// Read a private/internal instance field by name via reflection.
        /// The DCGO <see cref="MainPhaseAction"/> subclasses keep their
        /// payload fields private (no public accessor), so reflection is the
        /// only way to read them without modifying DCGO upstream files. The
        /// alternative would be to make the fields public or add internal
        /// getters — both expand the patch surface unnecessarily.
        ///
        /// Reflection cost: ~hundreds of nanoseconds per call. We hit this
        /// path once per main-phase action emission (~50-200 per game). Not
        /// a perf concern.
        /// </summary>
        private static T ReadField<T>(object instance, string name)
        {
            if (instance == null) return default;
            var field = instance.GetType().GetField(
                name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null) return default;
            object value = field.GetValue(instance);
            return value is T tv ? tv : default;
        }

        /// <summary>
        /// Untyped reflection read, returning the boxed field value as
        /// <see cref="object"/>. Used for debug-string rendering of fields
        /// whose runtime type doesn't match the convenient typed reader
        /// (e.g., enums boxed as their concrete type, not <c>int</c>).
        /// </summary>
        private static object ReadFieldAsObject(object instance, string name)
        {
            if (instance == null) return null;
            var field = instance.GetType().GetField(
                name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.GetValue(instance);
        }
    }
}
