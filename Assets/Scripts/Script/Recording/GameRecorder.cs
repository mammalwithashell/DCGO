using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Digimon.Recording
{
    /// <summary>
    /// Records every decision made by either player during a DCGO game into a
    /// JSONL file, encoded as 2192-action-space IDs. The Rust replay harness
    /// (<c>code/tools/dcgo-replay/</c>) consumes these recordings to validate
    /// engine parity; the BC dataset emitter consumes the same recordings to
    /// produce behavioral-cloning seed data.
    ///
    /// One MonoBehaviour instance is bootstrapped at scene-load time via
    /// <see cref="Bootstrap"/> and made discoverable through the static
    /// <see cref="Instance"/> accessor. Call sites in DCGO use the
    /// null-conditional pattern so the absence of the recorder is benign:
    ///
    ///   <c>GameRecorder.Instance?.LogAction(actor, action);</c>
    ///
    /// Schema:
    ///   <c>game_start</c>  — header row, emitted once per game with both decks
    ///   <c>action</c>      — one row per decision, with <c>actor</c>, <c>action_id</c>, <c>phase</c>,
    ///                        and (added post-v1, field is optional) <c>memory</c>
    ///   <c>selection</c>   — a semantic selection answer; also carries <c>memory</c>
    ///   <c>initial_state</c> — (added post-v1, optional) post-mulligan zone snapshot,
    ///                        emitted once per game — see <see cref="LogInitialState"/>
    ///   <c>encoder_failure</c> — sentinel for decisions the encoder cannot yet map
    ///   <c>game_end</c>    — terminal row with winner and reason
    ///
    /// <c>memory</c> convention (see <see cref="AppendMemory"/>): the shared
    /// memory gauge, converted to THIS RECORDING's <c>my_player_id</c>
    /// perspective — positive favors the recording player, negative favors
    /// the opponent. Always relative to the same fixed player for the whole
    /// recording, never to whoever is turn-player at that row, so a reader
    /// never has to re-derive whose favor a value means. Omitted entirely on
    /// rows from older recorders (parses as absent, not zero).
    ///
    /// See <c>openspec/changes/add-dcgo-recording-parity-harness/specs/dcgo-parity-harness/spec.md</c>
    /// and <c>docs/DCGO_RECORDING_SCHEMA.md</c> for the authoritative schema definition.
    /// </summary>
    public sealed class GameRecorder : MonoBehaviour
    {
        // ── Lifecycle / bootstrap ─────────────────────────────────────────

        public static GameRecorder Instance { get; private set; }

        public RecorderConfig Config { get; private set; } = new RecorderConfig();

        // [Harness mod] Path of the JSONL file for the game in progress, or ""
        // when idle. JobResultWriter.FileResult reads this to fill the
        // result sidecar's recording_path so the Rust replay/parity tooling
        // can find the recording a given job produced.
        /// <summary>Path of the JSONL file for the game in progress, or "" when idle.</summary>
        public string CurrentRecordingPath { get; private set; } = "";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            // One instance, persists across scene loads (Opening → BattleScene).
            // Guard against double-bootstrap; Unity may invoke this method
            // multiple times under domain-reload edge cases.
            if (Instance != null) return;

            var go = new GameObject("GameRecorder");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<GameRecorder>();
        }

        // ── State ─────────────────────────────────────────────────────────

        private StreamWriter _writer;
        private string _currentRecordingPath;
        private int _stepIndex;
        private int _rowsSinceFlush;
        private bool _gameInProgress;
        private string _gameId;

        // ── Public API: lifecycle ─────────────────────────────────────────

        /// <summary>
        /// Begin a new recording. Called from <c>TurnStateMachine.StartGame</c>
        /// once both players' decks have been finalized and the game-loop is
        /// about to execute.
        /// </summary>
        /// <param name="myPlayerId">The local player's ID (0 or 1) — i.e.
        /// <c>GManager.instance.You.PlayerID</c>.</param>
        /// <param name="myDeckCardIds">Local player's post-shuffle deck order
        /// (card IDs like "BT15-104", in the order they will be drawn).</param>
        /// <param name="oppDeckCardIds">Opponent's post-shuffle deck order, or
        /// <c>null</c> for PvP (opaque-opponent mode for the Rust replay).</param>
        /// <param name="isAi">True if this is a Bot Match.</param>
        /// <param name="oppDecklistComposition">For PvP only: the opponent's
        /// full decklist as an unordered multiset (composition without
        /// order). Read from <c>Opponent.PhotonPlayer.CustomProperties</c>
        /// under the <c>"BattleDeckData"</c> key — DCGO publishes both
        /// players' decklists there during room setup. <c>null</c> for
        /// Bot Match (the opponent's deck IS observable, so
        /// <c>oppDeckCardIds</c> carries it instead).</param>
        /// <param name="myEggDeck">Local player's post-shuffle digitama
        /// (egg) deck order — index 0 is hatched first. <c>null</c> from
        /// older callers (field then absent from the row).</param>
        /// <param name="oppEggDeck">Opponent's post-shuffle digitama deck
        /// order. Bot Match only; <c>null</c> for PvP (the opponent's
        /// digitama order is not observable, same as their main deck).</param>
        public void LogGameStart(int myPlayerId, IList<string> myDeckCardIds,
                                 IList<string> oppDeckCardIds, bool isAi,
                                 IList<string> oppDecklistComposition = null,
                                 int firstPlayerId = -1,
                                 IList<string> myEggDeck = null,
                                 IList<string> oppEggDeck = null)
        {
            if (!Config.Enabled) return;
            if (isAi && !Config.RecordBotMatches) return;
            if (!isAi && !Config.RecordPvPMatches) return;

            // Defensive: if a previous game didn't end cleanly (crash, force-quit,
            // exception in mid-game logging), close out gracefully before starting
            // a new one so we don't leak file handles.
            if (_gameInProgress)
            {
                Debug.LogWarning("[GameRecorder] LogGameStart called while a prior game " +
                                 "is still open; force-closing the prior recording.");
                CloseCurrentRecording(forceWinner: -1, reason: "interrupted");
            }

            _gameId = Guid.NewGuid().ToString("N");
            _stepIndex = 0;
            _rowsSinceFlush = 0;
            _gameInProgress = true;
            // [Harness mod] Reset before attempting to open this game's file.
            // Left as-is (NOT cleared) across LogGameEnd/CloseCurrentRecording
            // for the game that just finished -- JobResultWriter.FileResult
            // reads it AFTER LogGameEnd but BEFORE this method runs again for
            // the next job, so the just-completed path must still be visible
            // then. Clearing it here, right before the next attempt, is what
            // stops a later job from reporting a stale (previous game's) path
            // if ITS OWN file then fails to open below.
            CurrentRecordingPath = "";

            try
            {
                Directory.CreateDirectory(Config.ResolvedOutputDirectory);
                var timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ",
                                                        CultureInfo.InvariantCulture);
                _currentRecordingPath = Path.Combine(
                    Config.ResolvedOutputDirectory,
                    $"{timestamp}_{_gameId}.jsonl");
                _writer = new StreamWriter(_currentRecordingPath, append: false, new UTF8Encoding(false));
                // [Harness mod] Only publish the path once the writer has
                // actually opened -- see the reset comment above.
                CurrentRecordingPath = _currentRecordingPath;
            }
            catch (Exception e)
            {
                Debug.LogError($"[GameRecorder] failed to open recording file: {e.Message}");
                _gameInProgress = false;
                _writer = null;
                return;
            }

            var sb = new StringBuilder(256);
            sb.Append('{');
            AppendKv(sb, "v", ActionSpace.SCHEMA_VERSION); sb.Append(',');
            AppendKv(sb, "type", "game_start");           sb.Append(',');
            AppendKv(sb, "game_id", _gameId);             sb.Append(',');
            AppendKv(sb, "timestamp", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)); sb.Append(',');
            AppendKv(sb, "my_player_id", myPlayerId);     sb.Append(',');
            // Who takes turn 1. At StartGame time DCGO's gameContext has the
            // first player as NonTurnPlayer (see 先攻・後攻の決定 region).
            // -1 = unknown (older callers); harness falls back to inferring
            // from the mulligan order.
            if (firstPlayerId >= 0)
            {
                AppendKv(sb, "first_player", firstPlayerId); sb.Append(',');
            }
            AppendKv(sb, "is_ai", isAi);                  sb.Append(',');
            AppendKvArray(sb, "my_deck_post_shuffle", myDeckCardIds); sb.Append(',');
            if (oppDeckCardIds == null)
            {
                sb.Append("\"opp_deck_post_shuffle\":null");
            }
            else
            {
                AppendKvArray(sb, "opp_deck_post_shuffle", oppDeckCardIds);
            }
            if (oppDecklistComposition != null)
            {
                sb.Append(',');
                AppendKvArray(sb, "opp_decklist_composition", oppDecklistComposition);
            }
            if (myEggDeck != null)
            {
                sb.Append(',');
                AppendKvArray(sb, "my_egg_deck", myEggDeck);
            }
            if (oppEggDeck != null)
            {
                sb.Append(',');
                AppendKvArray(sb, "opp_egg_deck", oppEggDeck);
            }
            sb.Append('}');
            WriteRow(sb.ToString());
        }

        /// <summary>
        /// Log a card revealed from an opaque pile — the recorder writes
        /// one of these every time a card belonging to the opaque opponent
        /// becomes visible to the local client (draws, security pops,
        /// mill, peek effects).
        /// </summary>
        /// <param name="actor">PlayerId whose pile produced the reveal (i.e.
        /// the opaque opponent in PvP recordings).</param>
        /// <param name="cardId">The card identity now known (e.g. "BT15-104").</param>
        /// <param name="source">One of "draw", "security", "mill", "effect".
        /// Drives the engine's <c>RevealKind</c> tag at replay time.</param>
        public void LogReveal(int actor, string cardId, string source)
        {
            if (!_gameInProgress) return;
            if (string.IsNullOrEmpty(cardId))
            {
                Debug.LogWarning("[GameRecorder] LogReveal called with empty cardId; skipping.");
                return;
            }
            var sb = new StringBuilder(96);
            sb.Append('{');
            AppendKv(sb, "type", "reveal");      sb.Append(',');
            AppendKv(sb, "step", _stepIndex);    sb.Append(',');
            AppendKv(sb, "actor", actor);        sb.Append(',');
            AppendKv(sb, "card_id", cardId);     sb.Append(',');
            AppendKv(sb, "source", source ?? "effect");
            sb.Append('}');
            WriteRow(sb.ToString());
            _stepIndex++;
        }

        /// <summary>
        /// End the current recording. Called from <c>TurnStateMachine.EndGame</c>.
        /// </summary>
        /// <param name="winnerPlayerId">0 or 1; -1 if no winner (disconnect / draw).</param>
        /// <param name="reason">Free-text reason: "security_zero", "deck_out",
        /// "concede", "disconnect", etc. Recorded verbatim; replay harness
        /// keys cross-game stats by this field.</param>
        public void LogGameEnd(int winnerPlayerId, string reason)
        {
            if (!_gameInProgress) return;
            CloseCurrentRecording(winnerPlayerId, reason);
        }

        /// <summary>
        /// Log the post-mulligan zone snapshot. Called ONCE per game, from
        /// <c>TurnStateMachine.StartGame</c> immediately after BOTH players'
        /// mulligan decisions have resolved and security has been dealt.
        ///
        /// Closes a real gap: rule 5-2-1-5 of the official rules manual makes
        /// a mulligan a TRUE reshuffle ("the player returns their entire hand
        /// to their deck, shuffles it, then draws 5 cards for their new
        /// initial hand") — so <c>game_start</c>'s <c>my_deck_post_shuffle</c>
        /// (captured BEFORE mulligan) does not reflect a mulliganed game's
        /// actual post-mulligan order. Without this row, the Rust replay
        /// harness can only re-simulate the mulligan through its OWN RNG,
        /// which cannot reproduce DCGO's actual redraw.
        ///
        /// <b>Ordering convention</b>: card-id lists use the SAME "index 0 =
        /// first drawn / top" convention as <c>game_start</c>'s
        /// <c>my_deck_post_shuffle</c> (see <see cref="LogGameStart"/>) —
        /// NOT the Rust native recorder's opposite, bottom-first convention.
        /// The Rust <c>DcgoAdapter</c> reverses these lists itself before
        /// laying them into its pop-from-end zones; do not pre-reverse here.
        /// <c>initialHand</c> has no top/bottom concept and is passed as-is.
        ///
        /// <b>Player-id convention</b>: <paramref name="firstPlayerId"/> is 0
        /// or 1 — DCGO's OWN convention (matches <c>game_start</c>'s
        /// <c>my_player_id</c> / <c>first_player</c>), explicitly NOT the
        /// Rust native recorder's opposite 1/2 (Python) convention. Do not
        /// translate it before passing in.
        ///
        /// <c>opp*</c> parameters are <c>null</c> for PvP (the opponent's
        /// post-mulligan hand/library isn't observable — same visibility
        /// split as <c>opp_deck_post_shuffle</c>), populated for Bot Match.
        /// </summary>
        public void LogInitialState(
            int firstPlayerId,
            IList<string> myLibraryOrder,
            IList<string> myDigitamaLibraryOrder,
            IList<string> mySecurityOrder,
            IList<string> myInitialHand,
            IList<string> oppLibraryOrder = null,
            IList<string> oppDigitamaLibraryOrder = null,
            IList<string> oppSecurityOrder = null,
            IList<string> oppInitialHand = null)
        {
            if (!_gameInProgress || _writer == null) return;
            var sb = new StringBuilder(384);
            sb.Append('{');
            AppendKv(sb, "type", "initial_state");        sb.Append(',');
            AppendKv(sb, "first_player_id", firstPlayerId); sb.Append(',');
            sb.Append("\"my\":{");
            AppendKvArray(sb, "library_order", myLibraryOrder); sb.Append(',');
            AppendKvArray(sb, "digitama_library_order", myDigitamaLibraryOrder ?? Array.Empty<string>()); sb.Append(',');
            AppendKvArray(sb, "security_order", mySecurityOrder); sb.Append(',');
            AppendKvArray(sb, "initial_hand", myInitialHand);
            sb.Append('}');
            if (oppLibraryOrder != null)
            {
                sb.Append(',').Append("\"opp\":{");
                AppendKvArray(sb, "library_order", oppLibraryOrder); sb.Append(',');
                AppendKvArray(sb, "digitama_library_order", oppDigitamaLibraryOrder ?? Array.Empty<string>()); sb.Append(',');
                AppendKvArray(sb, "security_order", oppSecurityOrder ?? Array.Empty<string>()); sb.Append(',');
                AppendKvArray(sb, "initial_hand", oppInitialHand ?? Array.Empty<string>());
                sb.Append('}');
            }
            AppendMemory(sb);
            sb.Append('}');
            WriteRow(sb.ToString());
            // Deliberately does NOT increment `_stepIndex` -- this row isn't a
            // decision (nothing to number against); numbering the surrounding
            // action/selection rows must stay unaffected by whether a given
            // recording happens to carry this snapshot.
        }

        // ── Public API: per-decision logging ──────────────────────────────

        /// <summary>
        /// Log a main-phase action (one of the six <c>MainPhaseAction</c> subclasses).
        /// Called from inside <c>TurnStateMachine.QueueMainPhaseAction</c> immediately
        /// before the <c>photonView.RPC</c> dispatch.
        /// </summary>
        public void LogAction(int actorPlayerId, MainPhaseAction action,
                              string phaseName, Player actorPlayer)
        {
            if (!_gameInProgress) return;
            var encoded = ActionEncoder.EncodeMainPhaseAction(actorPlayerId, action, actorPlayer);
            EmitDecisionRow(actorPlayerId, encoded, phaseName, source: "main_phase");

            // PlayCardAction can baked-in digivolution sources; surface them as
            // explicit subsequent rows so the replay stream stays faithful to
            // our 2192-space action decomposition (one card play + N source picks).
            foreach (var extra in ActionEncoder.DecomposePlayCardExtras(action, actorPlayerId))
            {
                EmitDecisionRow(actorPlayerId, extra, phaseName, source: "play_card_extra");
            }
        }

        /// <summary>
        /// Log a selection response (int-valued; covers all <c>SelectIntSelection</c>
        /// callers). Called from inside <c>UserSelectionManager.SetIntForPlayer</c>.
        /// </summary>
        public void LogSelectionInt(int actorPlayerId, int value, string phaseName)
        {
            if (!_gameInProgress) return;
            var encoded = ActionEncoder.EncodeSelectionInt(actorPlayerId, value, phaseName);
            EmitDecisionRow(actorPlayerId, encoded, phaseName, source: "selection_int");
        }

        /// <summary>
        /// Log a selection response (bool-valued; covers all <c>SetBoolSelection</c>
        /// callers — yes/no prompts, optional triggers). Called from inside
        /// <c>UserSelectionManager.SetBoolForPlayer</c>.
        /// </summary>
        public void LogSelectionBool(int actorPlayerId, bool value, string phaseName)
        {
            if (!_gameInProgress) return;
            var encoded = ActionEncoder.EncodeSelectionBool(actorPlayerId, value, phaseName);
            EmitDecisionRow(actorPlayerId, encoded, phaseName, source: "selection_bool");
        }

        /// <summary>
        /// Log a mulligan decision. Called from <c>TurnStateMachine.SetRedraw</c>.
        /// </summary>
        public void LogMulligan(int actorPlayerId, bool redrew)
        {
            if (!_gameInProgress) return;
            var encoded = ActionEncoder.EncodeMulligan(redrew);
            EmitDecisionRow(actorPlayerId, encoded, phase: "Mulligan", source: "mulligan");
        }

        /// <summary>
        /// Log a breeding-phase decision (hatch / move / decline), already
        /// resolved to its engine action ID by the caller
        /// (<c>TurnStateMachine.SetBreedingPhase</c> — the single chokepoint
        /// all human/auto/bot breeding decisions funnel through).
        /// </summary>
        public void LogBreedingAction(int actorPlayerId, ushort actionId, string phaseName)
        {
            if (!_gameInProgress) return;
            EmitDecisionRow(actorPlayerId, ActionEncoder.Encoded.Ok(actionId),
                            phaseName, source: "breeding");
        }

        /// <summary>
        /// Log a selection answer with SEMANTIC payload (task 3.5). The Rust
        /// harness resolves these against its live PendingSelection, where
        /// candidate ordering and the action-id scheme are authoritative —
        /// so this row carries absolute identities, not action IDs.
        /// All payload arguments optional; pass only what the prompt knows.
        /// `targets` are (absolutePlayerId, dcgoFrameId) pairs; frame -1
        /// means "the player / security" (attack-target sentinel).
        /// </summary>
        public void LogSelectionRow(int actorPlayerId, string prompt, string phaseName,
                                    IList<KeyValuePair<int, int>> targets = null,
                                    IList<string> cardIds = null,
                                    IList<int> indexes = null,
                                    int? count = null,
                                    IList<int> candidates = null,
                                    long? intValue = null,
                                    bool? boolValue = null,
                                    bool cancel = false)
        {
            if (!_gameInProgress || _writer == null) return;
            var sb = new StringBuilder(192);
            sb.Append('{');
            AppendKv(sb, "type", "selection");        sb.Append(',');
            AppendKv(sb, "step", _stepIndex++);       sb.Append(',');
            AppendKv(sb, "actor", actorPlayerId);     sb.Append(',');
            AppendKv(sb, "prompt", prompt);           sb.Append(',');
            AppendKv(sb, "phase", phaseName ?? "Unknown");
            if (targets != null)
            {
                sb.Append(',').Append("\"targets\":[");
                for (int i = 0; i < targets.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"player\":").Append(targets[i].Key)
                      .Append(",\"frame\":").Append(targets[i].Value).Append('}');
                }
                sb.Append(']');
            }
            if (cardIds != null)
            {
                sb.Append(',');
                AppendKvArray(sb, "card_ids", cardIds);
            }
            if (indexes != null)
            {
                sb.Append(',').Append("\"indexes\":[");
                for (int i = 0; i < indexes.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(indexes[i]);
                }
                sb.Append(']');
            }
            if (count.HasValue)     { sb.Append(','); AppendKv(sb, "count", count.Value); }
            if (candidates != null && candidates.Count > 0)
            {
                sb.Append(',').Append("\"candidates\":[");
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(candidates[i]);
                }
                sb.Append(']');
            }
            if (intValue.HasValue)  { sb.Append(',').Append("\"int_value\":").Append(intValue.Value); }
            if (boolValue.HasValue) { sb.Append(','); AppendKv(sb, "bool_value", boolValue.Value); }
            if (cancel)             { sb.Append(','); AppendKv(sb, "cancel", true); }
            AppendMemory(sb);
            AppendBoards(sb);
            sb.Append('}');
            WriteRow(sb.ToString());
        }

        // ── Internals ─────────────────────────────────────────────────────

        private void EmitDecisionRow(int actor, ActionEncoder.Encoded encoded,
                                     string phase, string source)
        {
            var sb = new StringBuilder(160);
            sb.Append('{');
            if (encoded.IsFailure)
            {
                AppendKv(sb, "type", "encoder_failure"); sb.Append(',');
                AppendKv(sb, "step", _stepIndex);        sb.Append(',');
                AppendKv(sb, "actor", actor);            sb.Append(',');
                AppendKv(sb, "phase", phase ?? "");      sb.Append(',');
                AppendKv(sb, "source", source);          sb.Append(',');
                AppendKv(sb, "reason", encoded.FailureReason ?? "unknown"); sb.Append(',');
                AppendKv(sb, "raw_value", encoded.RawDebugValue);
            }
            else
            {
                AppendKv(sb, "type", "action");                  sb.Append(',');
                AppendKv(sb, "step", _stepIndex);                sb.Append(',');
                AppendKv(sb, "actor", actor);                    sb.Append(',');
                AppendKv(sb, "action_id", encoded.ActionId);     sb.Append(',');
                AppendKv(sb, "phase", phase ?? "");               sb.Append(',');
                AppendKv(sb, "source", source);
                AppendMemory(sb);
                AppendBoards(sb);
            }
            sb.Append('}');
            WriteRow(sb.ToString());
            _stepIndex++;
        }

        /// <summary>
        /// Append the shared memory gauge, converted to THIS RECORDING's
        /// <c>my_player_id</c> perspective (i.e. the LOCAL client's own
        /// <c>GManager.instance.You</c> — see <see cref="LogGameStart"/>'s
        /// doc: <c>myPlayerId == GManager.instance.You.PlayerID</c>).
        ///
        /// Convention: positive favors the recording player, negative
        /// favors the opponent. This is DCGO's own
        /// <c>Player.MemoryForPlayer</c> getter (see <c>Player.cs</c>) — it
        /// already converts the shared, single <c>GameContext.Memory</c>
        /// gauge (which is stored positive-favors-PlayerID-1, negated for
        /// PlayerID 0) into "as seen by this player" form. Emitting the
        /// ALREADY-perspective-converted value here — always relative to
        /// the SAME fixed player for the whole recording, never to
        /// whoever is turn-player at a given row — means a reader never
        /// has to re-derive whose favor a bare number means; a wrong
        /// guess there would silently invert every comparison.
        ///
        /// No-op (field omitted, not zero) when <c>GManager.instance.You</c>
        /// is unavailable, so rows stay well-formed outside a live game —
        /// same defensive pattern as <see cref="AppendBoards"/>.
        /// </summary>
        private static void AppendMemory(StringBuilder sb)
        {
            var you = GManager.instance?.You;
            if (you == null) return;
            sb.Append(',');
            AppendKv(sb, "memory", you.MemoryForPlayer);
        }

        /// <summary>
        /// Append <c>board_p0</c> / <c>board_p1</c> — both players' battle
        /// areas as card IDs, in the compact order this row's board operands
        /// index.
        ///
        /// DCGO's compact order follows on-screen frame position and
        /// permanents migrate between frames at runtime, while the Rust
        /// engine's battle area is in play order, so slot N means different
        /// permanents on the two sides. Recording the identities lets the
        /// replay harness rebuild the mapping rather than assume the orders
        /// agree (they routinely do not).
        ///
        /// No-op when the game context is unavailable, so rows stay
        /// well-formed outside a live game.
        /// </summary>
        private void AppendBoards(StringBuilder sb)
        {
            var gc = GManager.instance?.turnStateMachine?.gameContext;
            if (gc == null) return;

            for (int pid = 0; pid <= 1; pid++)
            {
                Player p = null;
                if (gc.TurnPlayer != null && gc.TurnPlayer.PlayerID == pid) p = gc.TurnPlayer;
                else if (gc.NonTurnPlayer != null && gc.NonTurnPlayer.PlayerID == pid) p = gc.NonTurnPlayer;
                if (p == null) continue;

                sb.Append(',');
                AppendKvArray(sb, pid == 0 ? "board_p0" : "board_p1",
                              ActionEncoder.BattleAreaCardIds(p));
            }
        }

        private void WriteRow(string json)
        {
            if (_writer == null) return;
            try
            {
                _writer.WriteLine(json);
                _rowsSinceFlush++;
                if (_rowsSinceFlush >= Config.FlushEveryNRows)
                {
                    _writer.Flush();
                    _rowsSinceFlush = 0;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[GameRecorder] write failed: {e.Message}");
                // Drop the writer to avoid further per-row error spam; the
                // recording is now corrupted, but DCGO keeps running.
                try { _writer.Dispose(); } catch { /* swallow */ }
                _writer = null;
                _gameInProgress = false;
            }
        }

        private void CloseCurrentRecording(int forceWinner, string reason)
        {
            try
            {
                if (_writer != null)
                {
                    var sb = new StringBuilder(96);
                    sb.Append('{');
                    AppendKv(sb, "type", "game_end"); sb.Append(',');
                    AppendKv(sb, "winner", forceWinner); sb.Append(',');
                    AppendKv(sb, "reason", reason ?? ""); sb.Append(',');
                    AppendKv(sb, "total_steps", _stepIndex);
                    sb.Append('}');
                    _writer.WriteLine(sb.ToString());
                    _writer.Flush();
                    _writer.Dispose();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GameRecorder] close failed: {e.Message}");
            }
            finally
            {
                _writer = null;
                _gameInProgress = false;
                _stepIndex = 0;
                _rowsSinceFlush = 0;
            }
        }

        private void OnApplicationQuit()
        {
            // Don't leave the file half-written if Unity is shutting down
            // mid-game (closing the editor, alt-F4 in standalone, etc.).
            if (_gameInProgress)
            {
                CloseCurrentRecording(forceWinner: -1, reason: "app_quit");
            }
        }

        // ── JSON formatting helpers ───────────────────────────────────────
        // We hand-format rather than pulling in JsonUtility / Newtonsoft so
        // the dependency footprint is zero and the wire format is stable
        // across Unity versions. Output is ASCII-only string escaping.

        private static void AppendKv(StringBuilder sb, string key, string value)
        {
            sb.Append('"'); sb.Append(key); sb.Append("\":");
            AppendQuotedString(sb, value);
        }
        private static void AppendKv(StringBuilder sb, string key, int value)
        {
            sb.Append('"'); sb.Append(key); sb.Append("\":");
            sb.Append(value.ToString(CultureInfo.InvariantCulture));
        }
        private static void AppendKv(StringBuilder sb, string key, uint value)
        {
            sb.Append('"'); sb.Append(key); sb.Append("\":");
            sb.Append(value.ToString(CultureInfo.InvariantCulture));
        }
        private static void AppendKv(StringBuilder sb, string key, bool value)
        {
            sb.Append('"'); sb.Append(key); sb.Append("\":");
            sb.Append(value ? "true" : "false");
        }
        private static void AppendKvArray(StringBuilder sb, string key, IList<string> values)
        {
            sb.Append('"'); sb.Append(key); sb.Append("\":[");
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0) sb.Append(',');
                AppendQuotedString(sb, values[i]);
            }
            sb.Append(']');
        }
        private static void AppendQuotedString(StringBuilder sb, string s)
        {
            if (s == null) { sb.Append("null"); return; }
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20)
                            sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:X4}", (int)c);
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
