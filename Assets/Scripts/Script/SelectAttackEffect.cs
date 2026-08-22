using Photon.Pun;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class SelectAttackEffect : MonoBehaviourPunCallbacks
{
    const int SecurityIndex = -1;
    const int NopIndex = -2;

    public void SetUp
        (
        Permanent attacker,
        Func<bool> canAttackPlayerCondition,
        Func<Permanent, bool> defenderCondition,
        ICardEffect cardEffect)
    {
        _attacker = attacker;
        _canAttackPlayerCondition = canAttackPlayerCondition;
        _defenderCondition = defenderCondition;
        _cardEffect = cardEffect;

        _canSelectNotAttack = true;
        _withoutTap = false;
        _isVortex = false;
        _customMessage = null;
        _customMessage_Enemy = null;
        _beforeOnAttackCoroutine = null;
        _afterOnAttackCoroutine = null;
    }

    public void SetUpCustomMessage(string customMessage, string customMessage_Enemy)
    {
        _customMessage = customMessage;
        _customMessage_Enemy = customMessage_Enemy;
    }

    public void SetCanNotSelectNotAttack()
    {
        _canSelectNotAttack = false;
    }

    public void SetWithoutTap()
    {
        _withoutTap = true;
    }
    
    public void SetIsVortex()
    {
        _isVortex = true;
    }

    public void SetBeforeOnAttackCoroutine(Func<IEnumerator> beforeOnAttackCoroutine)
    {
        _beforeOnAttackCoroutine = beforeOnAttackCoroutine;
    }

    public void SetAfterOnAttackCoroutine(Func<IEnumerator> afterOnAttackCoroutine)
    {
        _afterOnAttackCoroutine = afterOnAttackCoroutine;
    }

    Permanent _attacker = null;
    Func<bool> _canAttackPlayerCondition = null;
    Func<Permanent, bool> _defenderCondition = null;
    bool _canSelectNotAttack = true;
    bool _withoutTap = false;
    bool _isVortex = false;
    bool _noSelect = false;

    string _customMessage = null;
    string _customMessage_Enemy = null;

    Permanent _defender = null;
    ICardEffect _cardEffect = null;
    Func<IEnumerator> _beforeOnAttackCoroutine = null;
    Func<IEnumerator> _afterOnAttackCoroutine = null;

    #region ƒp[ƒ}ƒlƒ“ƒg‚ð‘I‘ð‚Å‚«‚é‚©
    bool CanTarget(Permanent permanent)
    {
        if (_attacker != null)
        {
            if (_attacker.TopCard != null)
            {
                if (_attacker.CanAttack(_cardEffect, _withoutTap, _isVortex))
                {
                    if (_attacker.CanAttackTargetDigimon(permanent, _cardEffect, _withoutTap, _isVortex))
                    {
                        if (_defenderCondition != null)
                        {
                            if (!_defenderCondition(permanent))
                            {
                                return false;
                            }
                        }

                        return true;
                    }
                }
            }

        }

        return false;
    }
    #endregion

    #region UŒ‚‚Å‚«‚é‘ŠŽèƒp[ƒ}ƒlƒ“ƒg‚ª‚¢‚é‚©
    public bool CanAttackDigimon()
    {
        foreach (Player player in GManager.instance.turnStateMachine.gameContext.Players)
        {
            foreach (Permanent permanent in player.GetFieldPermanents())
            {
                if (CanTarget(permanent))
                {
                    return true;
                }
            }
        }

        return false;
    }
    #endregion

    #region ƒvƒŒƒCƒ„[‚ÉUŒ‚‚ª‰Â”\‚©
    bool CanAttackPlayer()
    {
        if (_attacker != null)
        {
            if (_attacker.TopCard != null)
            {
                if (_attacker.CanAttack(_cardEffect, _withoutTap, _isVortex))
                {
                    if (_attacker.CanAttackTargetDigimon(null, _cardEffect, _withoutTap, _isVortex))
                    {
                        if (_canAttackPlayerCondition != null)
                        {
                            if (!_canAttackPlayerCondition())
                            {
                                return false;
                            }
                        }

                        return true;
                    }
                }
            }

        }

        return false;
    }
    #endregion

    #region ‘I‘ð‚ª‰Â”\‚©‚Ç‚¤‚©
    public bool active()
    {
        if (!CanAttackDigimon())
        {
            if (!CanAttackPlayer())
            {
                return false;
            }
        }

        if (GManager.instance.attackProcess.IsAttacking)
        {
            return false;
        }

        return true;
    }
    #endregion

    #region I—¹‚Å‚«‚é‚©”»’è
    bool CanEndSelect(Permanent selectedPermanent)
    {
        if (selectedPermanent == null)
        {
            if (!CanAttackPlayer())
            {
                return false;
            }
        }

        else
        {
            if (!CanAttackDigimon())
            {
                return false;
            }
        }

        return true;
    }
    #endregion

    public IEnumerator Activate()
    {
        _defender = null;
        _noSelect = false;

        if (_attacker == null) yield break;
        if (_attacker.TopCard == null) yield break;
        if (!_attacker.CanAttack(_cardEffect, _withoutTap, _isVortex)) yield break;

        if (active())
        {

            foreach (Player player in GManager.instance.turnStateMachine.gameContext.Players)
            {
                GManager.instance.turnStateMachine.OffFieldCardTarget(player);
                GManager.instance.turnStateMachine.OffHandCardTarget(player);
            }

            GManager.instance.turnStateMachine.IsSelecting = true;

            Player attackOwner = _attacker?.TopCard != null ? _attacker.TopCard.Owner : null;

            if (_attacker != null)
            {
                if (_attacker.TopCard != null)
                {
                    if (_attacker.CanAttack(_cardEffect, _withoutTap, _isVortex))
                    {
                        // [Harness mod] Auto mode drives both seats now; without
                        // this, the local seat's attack-target prompt would open
                        // UI and wait for a click that never comes. Route it to
                        // the AI branch below.
                        if (_attacker.TopCard.Owner.isYou && !Digimon.Harness.HarnessAuto.DrivesLocalSeat)
                        {
                            #region Select Attack Target
                            if (!string.IsNullOrEmpty(_customMessage))
                            {
                                GManager.instance.commandText.OpenCommandText(_customMessage);
                            }

                            else
                            {
                                if (CanAttackDigimon())
                                {
                                    GManager.instance.commandText.OpenCommandText("Which target will you attack?");
                                }

                                else
                                {
                                    GManager.instance.commandText.OpenCommandText("Will you attack to the opponent?");
                                }
                            }

                            #endregion

                            Permanent selectedPermanent = null;

                            #region Can Attack Player
                            if (CanAttackPlayer())
                            {
                                if (_attacker.TopCard.Owner.Enemy.SecurityCards.Count >= 1)
                                {
                                    _attacker.TopCard.Owner.Enemy.securityObject.securityBreakGlass.ShowBlueMatarial();
                                }

                                _attacker.TopCard.Owner.Enemy.securityObject.SetSecurityAttackObject();
                                _attacker.TopCard.Owner.Enemy.securityObject.SetSecurityOutline(true);

                                _attacker.TopCard.Owner.Enemy.securityObject.AddClickTarget(() =>
                                {
                                    selectedPermanent = null;
                                    EndSelect_RPC();
                                });

                            }
                            #endregion

                            List<FieldPermanentCard> candidates = new List<FieldPermanentCard>();

                            foreach (Player player in GManager.instance.turnStateMachine.gameContext.Players)
                            {
                                foreach (Permanent permanent in player.GetFieldPermanents())
                                {
                                    if (CanTarget(permanent))
                                    {
                                        permanent.ShowingPermanentCard.AddClickTarget(OnClickFieldPermanentCard);
                                        candidates.Add(permanent.ShowingPermanentCard);
                                    }
                                }
                            }

                            if (candidates.Count >= 1)
                            {
                                GManager.instance.hideCannotSelectObject.SetUpHideCannotSelectObject(candidates, false);
                            }

                            CheckEndSelect();

                            #region Selecting field permanent
                            void OnClickFieldPermanentCard(FieldPermanentCard fieldPermanentCard)
                            {
                                if (selectedPermanent == fieldPermanentCard.ThisPermanent)
                                {
                                    selectedPermanent = null;
                                }

                                else
                                {
                                    selectedPermanent = fieldPermanentCard.ThisPermanent;
                                }

                                CheckEndSelect();
                            }
                            #endregion

                            #region End Selectiong "Not Attack"
                            void CheckEndSelect()
                            {
                                #region CanEndSelect
                                if (CanEndSelect(selectedPermanent))
                                {

                                }

                                else
                                {

                                }
                                #endregion

                                #region Visually Select Target
                                foreach (Player player in GManager.instance.turnStateMachine.gameContext.Players)
                                {
                                    foreach (Permanent permanent in player.GetFieldPermanents())
                                    {
                                        permanent.ShowingPermanentCard.RemoveSelectEffect();

                                        if (CanTarget(permanent))
                                        {
                                            if (selectedPermanent == permanent)
                                            {
                                                permanent.ShowingPermanentCard.OnSelectEffect(1.1f);
                                                permanent.ShowingPermanentCard.SetOrangeOutline();
                                            }

                                            else
                                            {
                                                permanent.ShowingPermanentCard.OnSelectEffect(1.1f);
                                                permanent.ShowingPermanentCard.SetBlueOutline();
                                            }
                                        }
                                    }
                                }
                                #endregion

                                if (selectedPermanent == null)
                                {
                                    if (_canSelectNotAttack)
                                    {
                                        GManager.instance.BackButton.OpenSelectCommandButton("Not Attack", () => { photonView.RPC("SetAttackTarget", RpcTarget.All, attackOwner.PlayerID, false, NopIndex); }, 0);
                                    }

                                    GManager.instance.selectCommandPanel.CloseSelectCommandPanel();
                                }

                                else
                                {
                                    GManager.instance.selectCommandPanel.SetUpCommandButton(new List<Command_SelectCommand>() { new Command_SelectCommand("End Selection", EndSelect_RPC, 0) });

                                    GManager.instance.BackButton.CloseSelectCommandButton();
                                }
                            }

                            #endregion

                            #region End Selection RPC
                            void EndSelect_RPC()
                            {
                                foreach (Player player in GManager.instance.turnStateMachine.gameContext.Players)
                                {
                                    foreach (Permanent permanent in player.GetFieldPermanents())
                                    {
                                        permanent.ShowingPermanentCard.RemoveSelectEffect();
                                        permanent.ShowingPermanentCard.RemoveClickTarget();
                                    }
                                }

                                bool isTurnPlayer = false;
                                int PermanentIndex = SecurityIndex;

                                if (selectedPermanent != null)
                                {
                                    if (selectedPermanent.TopCard != null)
                                    {
                                        isTurnPlayer = selectedPermanent.TopCard.Owner == GManager.instance.turnStateMachine.gameContext.TurnPlayer;
                                        PermanentIndex = selectedPermanent.TopCard.Owner.GetFieldPermanents().IndexOf(selectedPermanent);
                                    }
                                }

                                photonView.RPC("SetAttackTarget", RpcTarget.All, attackOwner.PlayerID, isTurnPlayer, PermanentIndex);

                                GManager.instance.BackButton.CloseSelectCommandButton();
                            }
                            #endregion
                        }

                        else
                        {
                            #region Showing Enemy Message
                            if (!string.IsNullOrEmpty(_customMessage_Enemy))
                            {
                                GManager.instance.commandText.OpenCommandText(_customMessage_Enemy);
                            }

                            else
                            {
                                GManager.instance.commandText.OpenCommandText("The opponent is selecting the attack target.");
                            }
                            #endregion

                            #region AI
                            if (GManager.instance.IsAI)
                            {
                                List<Permanent> AttackcTargetCandidates = new List<Permanent>();

                                foreach (Player player in GManager.instance.turnStateMachine.gameContext.Players_ForTurnPlayer)
                                {
                                    foreach (Permanent permanent in player.GetFieldPermanents())
                                    {
                                        if (CanTarget(permanent))
                                        {
                                            AttackcTargetCandidates.Add(permanent);
                                        }
                                    }
                                }

                                Permanent selectedPermanent = null;

                                if (AttackcTargetCandidates.Count >= 1)
                                {
                                    if (!_attacker.CanAttackTargetDigimon(null, _cardEffect, _withoutTap, _isVortex) || (_attacker.TopCard.Owner.Enemy.SecurityCards.Count >= 3 && RandomUtility.IsSucceedProbability(0.5f)))
                                    {
                                        selectedPermanent = AttackcTargetCandidates[UnityEngine.Random.Range(0, AttackcTargetCandidates.Count)];
                                    }
                                }

                                bool isTurnPlayer = false;
                                int PermanentIndex = SecurityIndex;

                                if (selectedPermanent != null)
                                {
                                    if (selectedPermanent.TopCard != null)
                                    {
                                        isTurnPlayer = selectedPermanent.TopCard.Owner == GManager.instance.turnStateMachine.gameContext.TurnPlayer;
                                        PermanentIndex = selectedPermanent.TopCard.Owner.GetFieldPermanents().IndexOf(selectedPermanent);
                                    }
                                }

                                SetAttackTarget(attackOwner.PlayerID, isTurnPlayer, PermanentIndex);
                            }
                            #endregion
                        }
                    }
                }
            }

            //Wait until selection ends
            yield return new WaitUntil(() => attackOwner.HasPlayerSelection());

            _defender = null;
            bool isTargetTurnPlayer = false;
            int targetIndex = SecurityIndex;

            PermanentSelection permanentSelection = attackOwner.DequeuePlayerSelection<PermanentSelection>();

            if (permanentSelection != null)
            {
                if (permanentSelection.IsTurnPlayerList != null && permanentSelection.IsTurnPlayerList.Length > 0 &&
                        permanentSelection.PermanentIDList != null && permanentSelection.PermanentIDList.Length > 0)
                {
                    isTargetTurnPlayer = permanentSelection.IsTurnPlayerList[0];
                    targetIndex = permanentSelection.PermanentIDList[0];
                }

                _noSelect = targetIndex == NopIndex;

                if (_noSelect)
                {
                    GManager.instance.selectCommandPanel.CloseSelectCommandPanel();
                }
                else if (targetIndex != SecurityIndex)
                {
                    Player player = null;

                    if (isTargetTurnPlayer)
                    {
                        player = GManager.instance.turnStateMachine.gameContext.TurnPlayer;
                    }

                    else
                    {
                        player = GManager.instance.turnStateMachine.gameContext.NonTurnPlayer;
                    }

                    if (targetIndex >= 0 && targetIndex < player.GetFieldPermanents().Count)
                    {
                        _defender = player.GetFieldPermanents()[targetIndex];
                    }
                }
            }

            #region Clean up visual selections and UI
            foreach (Player player in GManager.instance.turnStateMachine.gameContext.Players)
            {
                GManager.instance.turnStateMachine.OffFieldCardTarget(player);
                GManager.instance.turnStateMachine.OffHandCardTarget(player);

                foreach (Permanent permanent in player.GetFieldPermanents())
                {
                    permanent.ShowingPermanentCard.RemoveSelectEffect();
                }

                player.securityObject.OffShowSecurityAttackObject();
                player.securityObject.RemoveClickTarget();

                if (_defender != null || _noSelect)
                {
                    player.securityObject.securityBreakGlass.gameObject.SetActive(false);
                }
            }

            GManager.instance.hideCannotSelectObject.Close();

            GManager.instance.selectCommandPanel.CloseSelectCommandPanel();
            GManager.instance.BackButton.CloseSelectCommandButton();

            GManager.instance.commandText.CloseCommandText();
            yield return new WaitWhile(() => GManager.instance.commandText.gameObject.activeSelf);

            #endregion

            if (!_noSelect)
            {
                if (CanEndSelect(_defender))
                {
                    yield return ContinuousController.instance.StartCoroutine(GManager.instance.attackProcess.Attack(_attacker, _defender, _cardEffect, _withoutTap, _beforeOnAttackCoroutine));

                    if(_afterOnAttackCoroutine != null)
                        yield return ContinuousController.instance.StartCoroutine(_afterOnAttackCoroutine());
                }
            }
        }
    }

    #region ‘I‘ðŒˆ’è
    /// <summary>
    /// [Harness mod - exam select] Candidate attack-target permanents for the
    /// scripted driver, in the same enumeration order the AI builds its
    /// AttackcTargetCandidates: every player's field (turn player first),
    /// filtered by <see cref="CanTarget"/>.
    /// </summary>
    private List<Permanent> ScriptedCandidatePermanents()
    {
        try
        {
            var perms = new List<Permanent>();
            foreach (Player player in GManager.instance.turnStateMachine.gameContext.Players_ForTurnPlayer)
            {
                foreach (Permanent permanent in player.GetFieldPermanents())
                {
                    if (CanTarget(permanent)) perms.Add(permanent);
                }
            }
            return perms;
        }
        catch (Exception)
        {
            // An absent candidate list means NOT MEASURED, never "none
            // offered" -- a step asserting candidates then fails loudly.
            return null;
        }
    }

    [PunRPC]
    public void SetAttackTarget(int playerID, bool isTurnPlayer, int permanentIndex)
    {
        // [Harness mod - exam select] A scripted line answers here, before the
        // recorder sees anything, so the recorded row carries what the script
        // asked for rather than the value the AI computed and we discard.
        // A false return is never "the script declined" -- TryAnswerStep has
        // already aborted the job on a mismatch -- so do not fall through.
        //
        // Answer shapes, per the exam wire:
        //   select_card_ids: [<top-card id>]  attack that permanent (identity
        //                                     matched against this prompt's
        //                                     own candidate list);
        //   select_value: -1                  attack the player / security;
        //   select_cancel: true               decline the attack (-2).
        // The side bit for the player/security case names the seat being
        // attacked -- the non-turn player, since attacks only happen on the
        // attacker's own turn; for decline it is meaningless.
        if (Digimon.Harness.InputDriver.IsActive)
        {
            List<Permanent> __perms = ScriptedCandidatePermanents();
            List<string> __candidateIds = null;
            if (__perms != null)
            {
                __candidateIds = new List<string>();
                foreach (Permanent __perm in __perms)
                {
                    __candidateIds.Add(__perm?.TopCard?.CardID ?? "");
                }
            }
            Digimon.Harness.HarnessJobStep __step;
            if (!Digimon.Harness.InputDriver.TryAnswerStep(
                    playerID, Digimon.Harness.InputDriver.KindSelectAttack,
                    1, __candidateIds, out __step))
            {
                return;
            }
            if (__step.select_cancel)
            {
                isTurnPlayer = false;
                permanentIndex = NopIndex;
            }
            else if (__step.select_value != int.MinValue)
            {
                if (__step.select_value != -1)
                {
                    Digimon.Harness.InputDriver.Abort(
                        "SelectAttackEffect: select_value must be -1 (attack the player), got " +
                        __step.select_value + "; permanent targets go by select_card_ids");
                    return;
                }
                isTurnPlayer = false;
                permanentIndex = SecurityIndex;
            }
            else if (__step.select_card_ids != null && __step.select_card_ids.Length > 0)
            {
                if (__step.select_card_ids.Length != 1)
                {
                    Digimon.Harness.InputDriver.Abort(
                        "SelectAttackEffect: an attack has exactly one target, got " +
                        __step.select_card_ids.Length + " select_card_ids");
                    return;
                }
                int[] __picks;
                string __err;
                if (!Digimon.Harness.SelectionAnswer.MatchCardIds(
                        __step.select_card_ids, __candidateIds, out __picks, out __err))
                {
                    Digimon.Harness.InputDriver.Abort("SelectAttackEffect: " + __err);
                    return;
                }
                Permanent __target = __perms[__picks[0]];
                if (__target == null || __target.TopCard == null)
                {
                    Digimon.Harness.InputDriver.Abort(
                        "SelectAttackEffect: matched candidate " + __picks[0] +
                        " has no top card, cannot address it");
                    return;
                }
                isTurnPlayer = __target.TopCard.Owner == GManager.instance.turnStateMachine.gameContext.TurnPlayer;
                permanentIndex = __target.TopCard.Owner.GetFieldPermanents().IndexOf(__target);
            }
            else
            {
                Digimon.Harness.InputDriver.Abort(
                    "SelectAttackEffect prompt needs select_card_ids, select_value: -1, " +
                    "or select_cancel, got: " + Digimon.Harness.SelectionAnswer.Describe(__step));
                return;
            }
        }

        // [Recording mod] attack-target pick. -2 = decline, -1 = player/security
        // (recorded as frame -1); otherwise the compact battle-area index,
        // which is what our action space targets (see ActionEncoder.ValidateFieldSlot).
        {
            var __gc = GManager.instance?.turnStateMachine?.gameContext;
            var __rec = Digimon.Recording.GameRecorder.Instance;
            if (__gc != null && __rec != null)
            {
                if (permanentIndex == -2)
                {
                    __rec.LogSelectionRow(playerID, "SelectAttackEffect", __gc.TurnPhase.ToString(), cancel: true);
                }
                else
                {
                    var __p = isTurnPlayer ? __gc.TurnPlayer : __gc.NonTurnPlayer;
                    int __frame = permanentIndex < 0 ? -1
                        : Digimon.Recording.ActionEncoder.ValidateFieldSlot(__p, permanentIndex);
                    __rec.LogSelectionRow(playerID, "SelectAttackEffect", __gc.TurnPhase.ToString(),
                        targets: new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<int, int>>
                            { new System.Collections.Generic.KeyValuePair<int, int>(__p.PlayerID, __frame) });
                }
            }
        }

        bool[] isTurnPlayerList = new bool[] { isTurnPlayer };
        int[] permanentIndexList = new int[] { permanentIndex };

        Player selectionPlayer = GManager.instance.GetPlayerFromID(playerID);

        if (selectionPlayer == null)
        {
            return;
        }

        selectionPlayer.QueuePlayerSelection(new PermanentSelection(isTurnPlayerList, permanentIndexList));
    }
    #endregion
}
