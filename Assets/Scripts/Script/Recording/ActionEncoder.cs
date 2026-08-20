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
                    return EncodePlayCard(play, actor);
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

        private static Encoded EncodePlayCard(PlayCardAction play, Player actor)
        {
            // PlayCardAction.CardIndex is an index into the per-game
            // gameContext.ActiveCardList (player 1's deck occupies 0..49,
            // player 2's 50..99) — NOT a hand slot. The Rust action space is
            // keyed on hand position, so resolve the physical card to its
            // current slot in the actor's hand. (CardSource.CardIndex is that
            // same ActiveCardList index — see SetPlayCard.)
            int cardIndex = ReadField<int>(play, "CardIndex");
            int handSlot = -1;
            if (actor != null)
            {
                for (int i = 0; i < actor.HandCards.Count; i++)
                {
                    if (actor.HandCards[i].CardIndex == cardIndex)
                    {
                        handSlot = i;
                        break;
                    }
                }
            }
            if (handSlot < 0 || handSlot >= ActionSpace.PLAY_HAND_END)
            {
                return Encoded.Fail("play_card_not_in_actor_hand",
                                    rawDebug: $"activeCardIndex={cardIndex} handSlot={handSlot}");
            }

            // A play onto an occupied frame is a digivolution in the Rust
            // action space; onto an empty frame (or no frame — options) it is
            // a base play.
            int targetFrame = ReadField<int>(play, "TargetFrameID");
            if (targetFrame >= 0 && actor != null
                && targetFrame < actor.fieldCardFrames.Count
                && !actor.fieldCardFrames[targetFrame].IsEmptyFrame())
            {
                // DCGO frame numbering: battle-area frames first (0..15),
                // then ONE breeding frame with the last ID (see the Player
                // fieldCardFrames constructor). The Rust action space has
                // battle slots 0..13 and BREEDING_TARGET = 14 (space.rs).
                //
                // NOTE the index-space shift: TargetFrameID is a SPARSE frame
                // id (it indexes fieldCardFrames / FieldPermanents, which have
                // null holes), whereas the engine's digivolve target is a
                // COMPACT battle-area position. Passing the frame id through
                // unchanged mis-targeted every battle-area digivolve whenever
                // a lower frame was empty. Breeding is exempt — it is a single
                // named slot on both sides.
                int breedingFrameId = actor.fieldCardFrames.Count - 1;
                int engineSlot;
                if (targetFrame == breedingFrameId)
                {
                    engineSlot = 14; // ActionSpace BREEDING_TARGET
                }
                else
                {
                    engineSlot = FrameIdToFieldSlot(actor, targetFrame);
                    if (engineSlot < 0 || engineSlot >= ActionSpace.MAX_FIELD_SLOTS)
                    {
                        // More than 14 occupied battle slots has no engine
                        // equivalent (engine caps at MAX_FIELD_SLOTS).
                        return Encoded.Fail("digivolve_frame_beyond_engine_slots",
                                            rawDebug: $"handSlot={handSlot} frame={targetFrame} slot={engineSlot}");
                    }
                }
                try
                {
                    return Encoded.Ok(ActionSpace.EncodeDigivolve(handSlot, engineSlot));
                }
                catch (System.ArgumentOutOfRangeException)
                {
                    return Encoded.Fail("digivolve_encode_out_of_range",
                                        rawDebug: $"handSlot={handSlot} frame={targetFrame}");
                }
            }

            return Encoded.Ok((ushort)(ActionSpace.PLAY_HAND_START + handSlot));
        }

        private static Encoded EncodeAttack(AttackPermanentAction atk, Player actor)
        {
            int attackerCompactIdx = ReadField<int>(atk, "PermanentIndex");
            int targetCompactIdx   = ReadField<int>(atk, "AttackTargetPermanentIndex");

            // Both indices are already compact battle-area positions, which is
            // exactly what the Rust action space targets. The Rust action space
            // is keyed on frame slot (0..13), not on the GetFieldPermanents
            // list position. AttackTargetPermanentIndex == -1 means "attack
            // security" — our SECURITY_TARGET (14) covers that.
            int attackerFrame = ValidateFieldSlot(actor, attackerCompactIdx);
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
                // target relative to the OPPONENT'S compact list, which is
                // the same index space our action-space target uses.
                var enemy = actor.Enemy;
                targetFrame = ValidateFieldSlot(enemy, targetCompactIdx);
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

            int slot = ValidateFieldSlot(actor, permCompactIdx);
            if (slot < 0)
                return Encoded.Fail("activate_permanent_frame_lookup_failed",
                                    rawDebug: permCompactIdx.ToString());

            if (slot >= ActionSpace.MAX_FIELD_SLOTS)
            {
                return Encoded.Fail("activate_permanent_out_of_range",
                                    rawDebug: $"slot={slot} skill={skillIdx}");
            }

            // DCGO's SkillIndex is POSITIONAL — an index into the permanent's
            // `EffectList(EffectTiming.OnDeclaration)` (see
            // TurnStateMachine.SetActSkill). Our per-permanent effect sub-slots
            // are SEMANTIC: 0 = Overclock (EndOfTurnAction phase), 2 = the
            // [Main] activated ability, 3 = DigiLink activate. Writing DCGO's
            // positional index straight into the sub-slot encoded a main-phase
            // activation as an Overclock, which the engine's mask never offers.
            //
            // This hook fires from QueueMainPhaseAction, so the activation is a
            // [Main] ability by construction → sub-slot FIELD_EFFECT_SLOT_FOR_MAIN.
            if (skillIdx != 0)
            {
                // Our action space reserves exactly one [Main] sub-slot per
                // permanent, so a card with two+ activated [Main] abilities is
                // not addressable. Fail loudly rather than mis-label it.
                return Encoded.Fail("activate_permanent_nonzero_skill_index",
                                    rawDebug: $"slot={slot} skill={skillIdx}");
            }

            return Encoded.Ok(
                ActionSpace.EncodeFieldEffect(slot, ActionSpace.FIELD_EFFECT_SLOT_FOR_MAIN));
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
        /// Bounds-check DCGO's <c>PermanentIndex</c> and return it unchanged.
        ///
        /// DCGO addresses board positions two different ways, and only one of
        /// them matches our action space:
        ///   - <c>Player.FieldPermanents</c> is a SPARSE array indexed by
        ///     <c>FieldCardFrame.FrameID</c> (the on-screen slot; empty frames
        ///     are null holes).
        ///   - <c>Player.GetFieldPermanents()</c> returns the COMPACT list of
        ///     non-null permanents in ascending frame order.
        ///
        /// Every gameplay packet we encode — <c>AttackPermanentAction</c>,
        /// <c>ActivatePermanentAction</c>, and the <c>SelectPermanentEffect</c>
        /// / <c>SelectAttackEffect</c> pickers — carries the COMPACT index
        /// (confirmed in <c>TurnStateMachine.SetActSkill</c> and
        /// <c>SetAttackingPermaent</c>, both of which index
        /// <c>GetFieldPermanents()</c> directly).
        ///
        /// Our action space also indexes the compact battle area (engine
        /// <c>Player.battle_area</c> is a packed Vec, and ATTACK / FIELD_EFFECT
        /// / selection targets are all positions within it). So the correct
        /// translation is the identity — this helper exists only to validate
        /// the range. An earlier version converted compact → FrameID, which
        /// silently emitted UI slot numbers (e.g. frame 4 for the sole
        /// permanent at battle-area index 0) and made every board reference in
        /// a recording unreplayable.
        ///
        /// Residual fidelity caveat: DCGO compacts by ascending FrameID while
        /// the engine's battle_area is in play order. Those agree whenever
        /// permanents are placed left-to-right, but a player who drops a card
        /// into a higher frame first can reorder the two lists. Such a game
        /// surfaces as an illegal_action / wrong-target divergence rather than
        /// silently mis-replaying.
        /// </summary>
        /// <returns>The compact index, or -1 if out of range.</returns>
        /// <summary>
        /// Snapshot a player's battle area as card IDs, in the same compact
        /// order every board operand in a recording indexes
        /// (<c>GetFieldPermanents()</c>).
        ///
        /// The replay harness needs this because DCGO's compact order is
        /// derived from on-screen frame position, and permanents migrate
        /// between frames at runtime (<c>PreferredFrame</c>), while the Rust
        /// engine's battle area is in play order. Slot N therefore means
        /// different permanents on the two sides. Recording the identities
        /// lets the harness rebuild the mapping instead of assuming the
        /// orders agree.
        /// </summary>
        internal static List<string> BattleAreaCardIds(Player player)
        {
            var ids = new List<string>();
            if (player == null) return ids;
            // GetBattleAreaPermanents, NOT GetFieldPermanents: the latter walks
            // every frame including the BREEDING one, so a hatched egg or a
            // digivolving stack showed up in the snapshot as though it were on
            // the battle field. The engine's battle_area holds no such thing, so
            // the two lists described different zones -- benign only because
            // DCGO orders the breeding frame last, which kept battle-area
            // indices aligned by luck rather than by construction.
            foreach (var perm in player.GetBattleAreaPermanents())
            {
                ids.Add(perm?.TopCard?.CardID ?? "");
            }
            return ids;
        }

        internal static int ValidateFieldSlot(Player player, int compactIndex)
        {
            if (player == null) return -1;
            var perms = player.GetFieldPermanents();
            if (compactIndex < 0 || compactIndex >= perms.Count) return -1;
            return compactIndex;
        }

        /// <summary>
        /// Convert a SPARSE DCGO <c>FieldCardFrame.FrameID</c> into the COMPACT
        /// battle-area position our action space uses — the inverse direction
        /// from <see cref="ValidateFieldSlot"/>.
        ///
        /// Needed for packets that carry a frame id rather than a compact index:
        /// <c>PlayCardAction.TargetFrameID</c> (the digivolve target) is the
        /// frame the card was dropped onto. Since <c>GetFieldPermanents()</c>
        /// compacts <c>FieldPermanents</c> in ascending frame order, the compact
        /// position is simply the count of occupied frames below this one.
        /// </summary>
        /// <returns>Compact battle-area index, or -1 if the frame is empty or
        /// out of range.</returns>
        internal static int FrameIdToFieldSlot(Player player, int frameId)
        {
            if (player == null || player.FieldPermanents == null) return -1;
            if (frameId < 0 || frameId >= player.FieldPermanents.Length) return -1;

            var occupant = player.FieldPermanents[frameId];
            if (occupant == null || occupant.TopCard == null) return -1;

            int slot = 0;
            for (int i = 0; i < frameId; i++)
            {
                var p = player.FieldPermanents[i];
                if (p != null && p.TopCard != null) slot++;
            }
            return slot;
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
