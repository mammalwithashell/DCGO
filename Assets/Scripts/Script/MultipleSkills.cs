using Photon.Pun;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class MultipleSkills : MonoBehaviourPunCallbacks
{
    public bool IsUsing { get; private set; } = false;
    AutoProcessing _autoProcessing = null;
    public List<SkillInfo> SkillInfos_used { get; private set; } = new List<SkillInfo>();
    public List<SkillInfo> StackedSkillInfos = new List<SkillInfo>();
    public bool IsOnlyHandEffectStacked => StackedSkillInfos.Every(skillInfo =>
        skillInfo.CardEffect != null && skillInfo.CardEffect.EffectSourceCard != null && CardEffectCommons.IsExistOnHand(skillInfo.CardEffect.EffectSourceCard) && skillInfo.CardEffect.EffectDiscription.Contains("[Hand]"));

    bool IsOnlyOptionalEffectStacked => StackedSkillInfos.Every(skillInfo => skillInfo.CardEffect != null && skillInfo.CardEffect.IsSkippable(null));
    bool IsEachStackedEffectHasDistinctSourceCard => StackedSkillInfos.Filter(skillInfo1 => skillInfo1.CardEffect != null && skillInfo1.CardEffect.EffectSourceCard != null)
        .Every(skillInfo => StackedSkillInfos.Filter(skillInfo1 => skillInfo1.CardEffect != null && skillInfo1.CardEffect.EffectSourceCard != null)
            .Count(otherSkillInfo => otherSkillInfo != skillInfo && skillInfo.CardEffect.EffectSourceCard == otherSkillInfo.CardEffect.EffectSourceCard) == 0);
    public IEnumerator ActivateMultipleSkills(List<SkillInfo> skillInfos, AutoProcessing autoProcessing, bool CheckNewTriggredSkill_mainStack, Func<List<SkillInfo>, SkillInfo, bool> skipCondition)
    {
        _skillIndex = 0;
        IsUsing = true;

        SkillInfos_used = new List<SkillInfo>();
        StackedSkillInfos = new List<SkillInfo>();

        _autoProcessing = autoProcessing;

        List<SkillInfo> TurnPlayerSkillInfos = new List<SkillInfo>();
        List<SkillInfo> NonTurnPlayerSkillInfos = new List<SkillInfo>();

        foreach (SkillInfo skillInfo in skillInfos)
        {
            if (skillInfo != null)
            {
                ICardEffect cardEffect = skillInfo.CardEffect;

                if (cardEffect.EffectSourceCard != null)
                {
                    if (cardEffect.EffectSourceCard.Owner == GManager.instance.turnStateMachine.gameContext.TurnPlayer)
                    {
                        TurnPlayerSkillInfos.Add(skillInfo);
                    }

                    else if (cardEffect.EffectSourceCard.Owner == GManager.instance.turnStateMachine.gameContext.NonTurnPlayer)
                    {
                        NonTurnPlayerSkillInfos.Add(skillInfo);
                    }
                }
            }
        }

        yield return ContinuousController.instance.StartCoroutine(ActivateMultipleSkills_OnePlayer(TurnPlayerSkillInfos, GManager.instance.turnStateMachine.gameContext.TurnPlayer, CheckNewTriggredSkill_mainStack, skipCondition));
        yield return ContinuousController.instance.StartCoroutine(ActivateMultipleSkills_OnePlayer(NonTurnPlayerSkillInfos, GManager.instance.turnStateMachine.gameContext.NonTurnPlayer, CheckNewTriggredSkill_mainStack, skipCondition));

        SkillInfos_used = new List<SkillInfo>();

        IsUsing = false;
    }

    int _skillIndex;

    #region [Harness mod - exam select] staged candidate list for SetTargetSkill
    // SetTargetSkill is a [PunRPC] and receives only (playerID, skillIndex);
    // skillInfos_active is a local of the coroutine that raised the prompt.
    // A scripted answer that names a trigger by IDENTITY therefore has nothing
    // to resolve against unless the coroutine hands the list over, so it does,
    // once per prompt, immediately after the list is finalized.
    //
    // Null means NOT MEASURED: the hook then refuses to resolve an identity or
    // to range-check an index rather than guessing at either. Reentrancy is
    // not a hazard -- AutoProcessing keeps a POOL of MultipleSkills components
    // and `availableMultipleSkills` hands out one that is not `IsUsing`, so a
    // nested resolution runs on a different instance with its own field.
    List<SkillInfo> _scriptedActiveSkillInfos;

    /// <summary>Source-card ids of the staged prompt, in prompt order.</summary>
    List<string> ScriptedCandidateCardIds()
    {
        if (_scriptedActiveSkillInfos == null) return null;

        List<string> ids = new List<string>();
        foreach (SkillInfo skillInfo in _scriptedActiveSkillInfos)
        {
            CardSource source = skillInfo != null && skillInfo.CardEffect != null
                ? skillInfo.CardEffect.EffectSourceCard : null;
            ids.Add(source == null ? "" : source.CardID);
        }
        return ids;
    }

    /// <summary>
    /// Human-legible dump of the staged prompt for abort messages: index, source
    /// card id, and DCGO's own effect name. The effect name is diagnostics only
    /// -- it is NOT part of the wire vocabulary -- but it is what lets an author
    /// see which of a card's several triggers each ordinal names.
    /// </summary>
    string DescribeScriptedCandidates()
    {
        if (_scriptedActiveSkillInfos == null) return "[not measured]";

        List<string> parts = new List<string>();
        for (int i = 0; i < _scriptedActiveSkillInfos.Count; i++)
        {
            SkillInfo skillInfo = _scriptedActiveSkillInfos[i];
            ICardEffect cardEffect = skillInfo == null ? null : skillInfo.CardEffect;
            CardSource source = cardEffect == null ? null : cardEffect.EffectSourceCard;
            string name = cardEffect == null ? "" : (cardEffect.EffectName ?? "");
            parts.Add(i + ":" + (source == null ? "<none>" : source.CardID)
                      + (string.IsNullOrEmpty(name) ? "" : " '" + name + "'"));
        }
        return "[" + string.Join(" | ", parts.ToArray()) + "]";
    }

    /// <summary>
    /// Whether every staged effect could legally be declined -- DCGO's own
    /// `_CanNoSelect` gate on the "Don't activate these effects" button. The
    /// scripted path never opens that panel, so the gate has to be re-checked
    /// here or a scripted cancel would drop MANDATORY triggers and play on.
    /// Null (NOT MEASURED) is not "yes".
    /// </summary>
    bool ScriptedStackIsAllSkippable()
    {
        if (_scriptedActiveSkillInfos == null) return false;

        foreach (SkillInfo skillInfo in _scriptedActiveSkillInfos)
        {
            if (skillInfo == null || skillInfo.CardEffect == null) return false;
            if (!skillInfo.CardEffect.IsSkippable(skillInfo.Hashtable)) return false;
        }
        return true;
    }
    #endregion

    bool IsCutinEffect(bool CheckNewTriggredSkill_mainStack) => !CheckNewTriggredSkill_mainStack && _autoProcessing != GManager.instance.autoProcessing;

    IEnumerator ActivateMultipleSkills_OnePlayer(List<SkillInfo> skillInfos, Player player, bool CheckNewTriggredSkill_mainStack, Func<List<SkillInfo>, SkillInfo, bool> skipCondition)
    {
        StackedSkillInfos = new List<SkillInfo>();

        foreach (SkillInfo skillInfo in skillInfos)
        {
            StackedSkillInfos.Add(skillInfo);
        }

        while (true)
        {
            List<SkillInfo> skillInfos_active = new List<SkillInfo>();

            foreach (SkillInfo skillInfo in StackedSkillInfos)
            {
                #region set the flag whether it is Digimon's effect or Tamer's effect
                if (skillInfo.CardEffect != null)
                {
                    if (skillInfo.CardEffect.EffectSourceCard != null)
                    {
                        CardSource card = skillInfo.CardEffect.EffectSourceCard;

                        if (card != null)
                        {
                            if (!skillInfo.CardEffect.IsDigimonEffect)
                            {
                                if (skillInfo.CardEffect.IsInheritedEffect || skillInfo.CardEffect.IsLinkedEffect)
                                {
                                    skillInfo.CardEffect.SetIsDigimonEffect(true);
                                }
                                else
                                {
                                    if (card.PermanentOfThisCard() != null)
                                    {
                                        skillInfo.CardEffect.SetIsDigimonEffect(card.PermanentOfThisCard().IsDigimon);
                                        skillInfo.CardEffect.SetIsTamerEffect(card.PermanentOfThisCard().IsTamer);
                                    }

                                    else
                                    {
                                        skillInfo.CardEffect.SetIsTamerEffect(card.IsTamer);
                                    }

                                    if (card == GManager.instance.attackProcess.SecurityDigimon)
                                    {
                                        skillInfo.CardEffect.SetIsDigimonEffect(true);
                                    }
                                }
                            }
                        }
                    }
                }
                #endregion

                #region Check if the effect can be activated
                if (!skillInfo.CardEffect.CanActivate(skillInfo.Hashtable))
                {
                    Debug.Log($"{skillInfo.CardEffect.EffectName} Can't Activate");
                    continue;
                }

                if (skipCondition != null)
                {
                    if (skipCondition(_autoProcessing.skillInfos_used, skillInfo))
                    {
                        Debug.Log($"{skillInfo.CardEffect.EffectName} has been skipped");

                        continue;
                    }
                }

                if (skillInfo.CardEffect.ChainActivations > 0)
                {
                    if (GManager.instance.autoProcessing.IsCutInEffectUsedMaxCount(skillInfo.CardEffect))
                    {
                        Debug.Log($"{skillInfo.CardEffect.EffectName} has exceeded its use");

                        continue;
                    }
                }

                if (IsCutinEffect(CheckNewTriggredSkill_mainStack))
                {
                    Debug.Log($"{skillInfo.CardEffect.EffectName} is Cut In effect");

                    if (GManager.instance.autoProcessing.IsCutInEffectHasUsed(skillInfo.CardEffect))
                    {
                        Debug.Log($"{skillInfo.CardEffect.EffectName} has been used");

                        continue;
                    }
                }

                skillInfos_active.Add(skillInfo);
                #endregion
            }

            skillInfos_active = skillInfos_active.Filter(skillInfo => skillInfo != null && skillInfo.CardEffect != null
                && skillInfo.CardEffect.CanActivate(skillInfo.Hashtable) && skillInfo.CardEffect.EffectSourceCard != null);

            // [Harness mod - exam select] Stage this prompt's candidate list for
            // SetTargetSkill, BEFORE any branch can raise the prompt.
            _scriptedActiveSkillInfos = skillInfos_active;

            if (skillInfos_active.Count > 0)
            {
                bool oldIsSecurityGlassBlue = player.securityObject.securityBreakGlass.IsBlueGlass && IsOnlyHandEffectStacked && IsOnlyOptionalEffectStacked && IsEachStackedEffectHasDistinctSourceCard;

                #region If the effect list is one, process normally.
                if (skillInfos_active.Count == 1)
                {
                    _skillIndex = 0;

                    yield return ContinuousController.instance.StartCoroutine(Activate(true));
                }
                #endregion

                #region If there are multiple effect lists, select which one to process first
                else
                {
                    List<CardSource> RootCardSources = skillInfos_active.Map(skillInfo => skillInfo.CardEffect.EffectSourceCard);


                    // Blast Digivolution
                    if (IsOnlyHandEffectStacked && IsOnlyOptionalEffectStacked && IsEachStackedEffectHasDistinctSourceCard)
                    {
                        // [Harness mod] Auto mode drives both seats now; without
                        // this, the local seat's effect-order prompt would open
                        // UI and wait for a click that never comes. Route it to
                        // the AI branch below.
                        if (player.isYou && !Digimon.Harness.HarnessAuto.DrivesLocalSeat)
                        {
                            int skillIndex = 0;

                            //if (!ContinuousController.instance.SkipSelectOrderOrCount)
                            {
                                if (oldIsSecurityGlassBlue)
                                {
                                    player.securityObject.securityBreakGlass.gameObject.SetActive(false);
                                }

                                SelectHandEffect selectHandEffect = GManager.instance.GetComponent<SelectHandEffect>();

                                selectHandEffect.SetUp(
                                    selectPlayer: player,
                                    canTargetCondition: (cardSource) => RootCardSources.Contains(cardSource),
                                    canTargetCondition_ByPreSelecetedList: null,
                                    canEndSelectCondition: null,
                                    maxCount: 1,
                                    canNoSelect: true,
                                    canEndNotMax: false,
                                    isShowOpponent: true,
                                    selectCardCoroutine: SelectCardCoroutine,
                                    afterSelectCardCoroutine: AfterSelectCardCoroutine,
                                    mode: SelectHandEffect.Mode.Custom,
                                    cardEffect: null);

                                selectHandEffect.SetUpCustomMessage("Multiple effects are triggered.\nChoose which effect to process first.", "");
                                selectHandEffect.SetNotShowCard();
                                selectHandEffect.SetNotShowOpponentMessage();
                                selectHandEffect.SetIsLocal();

                                yield return StartCoroutine(selectHandEffect.Activate());

                                IEnumerator SelectCardCoroutine(CardSource cardSource)
                                {
                                    for (int i = 0; i < skillInfos_active.Count; i++)
                                    {
                                        SkillInfo skillInfo = skillInfos_active[i];

                                        if (skillInfo.CardEffect.EffectSourceCard == cardSource)
                                        {
                                            skillIndex = i;
                                            break;
                                        }
                                    }

                                    yield return null;
                                }

                                IEnumerator AfterSelectCardCoroutine(List<CardSource> cardSources)
                                {
                                    if (cardSources.Count == 0)
                                    {
                                        skillIndex = -1;
                                    }

                                    yield return null;
                                }
                            }

                            photonView.RPC("SetTargetSkill", RpcTarget.All, player.PlayerID, skillIndex);
                        }

                        else
                        {
                            #region AI
                            if (GManager.instance.IsAI)
                            {
                                SetTargetSkill(player.PlayerID, 0);
                            }
                            #endregion
                        }
                    }

                    else
                    {
                        // [Harness mod] Auto mode drives both seats now; without
                        // this, the local seat's effect-order prompt would open
                        // UI and wait for a click that never comes. Route it to
                        // the AI branch below.
                        if (player.isYou && !Digimon.Harness.HarnessAuto.DrivesLocalSeat)
                        {
                            int skillIndex = -1;

                            if (!ContinuousController.instance.autoEffectOrder)
                            {
                                yield return StartCoroutine(GManager.instance.selectCardPanel.OpenSelectCardPanel(
                                Message: "Multiple effects are triggered.\nChoose which effect to process.",
                                NotSelectButtonMessage: "Don't activate these effects.",
                                EndSelectButtonMessage: "End Selection",
                                _OnClickNotSelectButtonAction: null,
                                _OnClickEndSelectButtonAction: null,
                                RootCardSources: RootCardSources,
                                _CanTargetCondition: (cardSource) => true,
                                _CanTargetCondition_ByPreSelecetedList: null,
                                _CanEndSelectCondition: null,
                                _MaxCount: 1,
                                _CanEndNotMax: false,
                                _CanNoSelect: () => skillInfos_active.All(skillInfo => skillInfo.CardEffect.IsSkippable(skillInfo.Hashtable)),
                                CanLookReverseCard: true,
                                skillInfos: skillInfos_active,
                                root: SelectCardEffect.Root.None));

                                if (GManager.instance.selectCardPanel.SelectedIndex.Count > 0)
                                {
                                    skillIndex = GManager.instance.selectCardPanel.SelectedIndex[0];
                                }
                                else
                                {
                                    foreach (FieldPermanentCard fieldPermanentCard in player.FieldPermanentObjects)
                                    {
                                        fieldPermanentCard.OffPermanentIndexText();
                                    }
                                }
                            }

                            else
                            {
                                skillIndex = AutomaticOrder.GetSkillIndexAutomaticOrder(skillInfos_active);
                            }

                            photonView.RPC("SetTargetSkill", RpcTarget.All, player.PlayerID, skillIndex);
                        }

                        else
                        {
                            if (!IsOnlyHandEffectStacked)
                            {
                                GManager.instance.commandText.OpenCommandText("The opponent is choosing which effect to process first.");
                            }

                            #region AI
                            if (GManager.instance.IsAI)
                            {
                                SetTargetSkill(player.PlayerID, 0);
                            }
                            #endregion
                        }
                    }

                    yield return new WaitUntil(() => player.HasPlayerSelection());

                    ValueSelection valueSelection = player.DequeuePlayerSelection<ValueSelection>();
                    _skillIndex = valueSelection != null ? valueSelection.ValueAsInt() : 0;

                    GManager.instance.commandText.CloseCommandText();
                    yield return new WaitWhile(() => GManager.instance.commandText.gameObject.activeSelf);

                    yield return ContinuousController.instance.StartCoroutine(Activate(!(IsOnlyHandEffectStacked && IsOnlyOptionalEffectStacked && IsEachStackedEffectHasDistinctSourceCard)));

                }
                #endregion

                #region Executing the effect
                IEnumerator Activate(bool isCheckOptional)
                {
                    // [Harness mod] Upstream's guard read `skillInfos_active.Count <
                    // _skillIndex`, which is off by one in the dangerous direction:
                    // _skillIndex == Count slipped through into the indexer below
                    // (IndexOutOfRangeException), and anything larger SILENTLY emptied
                    // StackedSkillInfos and yield-broke -- the outer while-loop then sees
                    // an empty stack, breaks, and the game plays on WITHOUT the remaining
                    // triggers. A game that continues wrong is worse than one that stops,
                    // and under a scripted line it is a wrong answer nobody sees, so an
                    // out-of-range index is a FINDING here.
                    if (_skillIndex >= skillInfos_active.Count)
                    {
                        if (Digimon.Harness.InputDriver.IsActive)
                        {
                            Digimon.Harness.InputDriver.Abort(
                                "MultipleSkills: skill index " + _skillIndex + " is out of range for " +
                                skillInfos_active.Count + " active effect(s) " + DescribeScriptedCandidates() +
                                "; DCGO would have cleared the whole trigger stack and played on without it");
                        }

                        StackedSkillInfos = new List<SkillInfo>();
                        yield break;
                    }

                    // -1 is DCGO's own "Don't activate these effects" -- a legitimate
                    // decline of a stack of optional effects, not an error.
                    if (_skillIndex < 0)
                    {
                        StackedSkillInfos = new List<SkillInfo>();
                        yield break;
                    }

                    if (oldIsSecurityGlassBlue)
                    {
                        player.securityObject.securityBreakGlass.gameObject.SetActive(false);
                    }

                    ICardEffect cardEffect = skillInfos_active[_skillIndex].CardEffect;
                    Hashtable hashtable = skillInfos_active[_skillIndex].Hashtable;

                    StackedSkillInfos.Remove(skillInfos_active[_skillIndex]);

                    skillInfos_active[_skillIndex].CardEffect.SetOnProcessCallbuck(() =>
                        {
                            SkillInfos_used.Add(skillInfos_active[_skillIndex]);
                            cardEffect.EffectSourceCard.cEntity_EffectController.RegisterUseEffectThisTurn(cardEffect);
                        });

                    if (cardEffect is ActivateICardEffect)
                    {
                        if (cardEffect.CanActivate(hashtable))
                        {
                            // For interrupt processing
                            if (IsCutinEffect(CheckNewTriggredSkill_mainStack))
                            {
                                // GManager.instance.autoProcessing.AddCutinEffect(cardEffect);

                                yield return ContinuousController.instance.StartCoroutine(((ActivateICardEffect)cardEffect)
                                .Activate_Optional_Effect_Execute(
                                    hashtable,
                                    isCheckOptional,
                                    useEffectCallback: GManager.instance.autoProcessing.AddCutinEffect));
                            }

                            else
                            {
                                yield return ContinuousController.instance.StartCoroutine(GManager.instance.autoProcessing.ActivateEffectProcess(
                                    cardEffect,
                                    hashtable,
                                    isCheckOptional));
                            }
                        }
                    }

                    if (oldIsSecurityGlassBlue)
                    {
                        player.securityObject.securityBreakGlass.ShowBlueMatarial();
                    }
                }
                #endregion

                //Rule processing
                yield return ContinuousController.instance.StartCoroutine(GManager.instance.autoProcessing.RuleProcess());

                if (GManager.instance.turnStateMachine.endGame)
                {
                    yield break;
                }

                #region If there are any newly triggered effects, resolve those first.
                if (!CheckNewTriggredSkill_mainStack)
                {
                    yield return ContinuousController.instance.StartCoroutine(_autoProcessing.TriggeredSkillProcess(CheckNewTriggredSkill_mainStack, skipCondition));
                }

                else
                {
                    yield return ContinuousController.instance.StartCoroutine(GManager.instance.autoProcessing.TriggeredSkillProcess(CheckNewTriggredSkill_mainStack, null));
                }
                #endregion
            }

            else
            {
                break;
            }
        }
    }

    [PunRPC]
    public void SetTargetSkill(int playerID, int skillIndex)
    {
        // [Harness mod - phase 2 / exam select] A scripted line answers here,
        // before the recorder sees anything, so the recorded row carries what
        // the script asked for rather than the value the AI computed and we
        // discard. A false return is never "the script declined" --
        // TryAnswerStep has already aborted the job on a mismatch -- so do not
        // fall through.
        //
        // IDENTITIES ON THE WIRE. `skillIndex` is a 0-based index into THIS
        // prompt's skillInfos_active -- DCGO's own list order -- while our
        // engine names a queued trigger by its own TriggerOrder slot. Passing
        // one value space off as the other picks a different trigger, and out
        // of range it makes DCGO CLEAR the entire trigger stack and play on
        // without it (see the guard in Activate above). So the preferred answer
        // names the trigger's SOURCE CARD via `select_card_ids` (exactly one
        // id) and is resolved here against the prompt's own candidates, the
        // same shape the other selection hooks use.
        //
        // A card with two stacked triggers (an [On Deletion] and an
        // <Ascension> on the same deleted carrier) offers the SAME identity
        // twice, and here that is the common case rather than an edge -- so
        // occurrence order is NOT assumed. `select_ordinal` then disambiguates
        // as the 0-based position AMONG that card's own candidates, and its
        // absence at an ambiguous prompt aborts with every candidate named.
        //
        // `select_value` alone remains supported as the raw DCGO skill index,
        // but it is now RANGE-CHECKED: out of range aborts as a finding instead
        // of silently clearing the stack. It never combines with
        // `select_card_ids` -- an index and an identity answering one prompt is
        // the value-space confusion this hook exists to end. `select_cancel` is
        // DCGO's "Don't activate these effects" (-1).
        if (Digimon.Harness.InputDriver.IsActive)
        {
            List<string> __candidateIds = ScriptedCandidateCardIds();

            Digimon.Harness.HarnessJobStep __step;
            if (!Digimon.Harness.InputDriver.TryAnswerStep(
                    playerID, Digimon.Harness.InputDriver.KindMultipleSkills,
                    1, __candidateIds, out __step))
            {
                return;
            }

            if (__step.select_cancel)
            {
                // DCGO only offers "Don't activate these effects" when every
                // stacked effect is skippable. The scripted path never opens
                // that panel, so re-check the gate: declining a MANDATORY
                // trigger stack would drop those triggers and play on, which is
                // the same silent-wrong-game the index range check prevents.
                if (!ScriptedStackIsAllSkippable())
                {
                    Digimon.Harness.InputDriver.Abort(
                        "MultipleSkills: select_cancel declines the whole trigger stack, but not " +
                        "every stacked effect is optional " + DescribeScriptedCandidates() +
                        " -- DCGO would not offer that choice here");
                    return;
                }
                skillIndex = -1;
            }
            else if (__step.select_card_ids != null && __step.select_card_ids.Length > 0)
            {
                if (__step.select_card_ids.Length != 1)
                {
                    Digimon.Harness.InputDriver.Abort(
                        "MultipleSkills is a single-pick prompt but the step names " +
                        __step.select_card_ids.Length + " cards: " +
                        Digimon.Harness.SelectionAnswer.Describe(__step));
                    return;
                }

                if (__step.select_value != int.MinValue)
                {
                    Digimon.Harness.InputDriver.Abort(
                        "MultipleSkills: select_value is the raw DCGO-index fallback and cannot " +
                        "combine with select_card_ids -- use select_ordinal to say WHICH of that " +
                        "card's own triggers. Got: " +
                        Digimon.Harness.SelectionAnswer.Describe(__step));
                    return;
                }

                int __pick;
                string __err;
                if (!Digimon.Harness.SelectionAnswer.MatchOneWithOrdinal(
                        __step.select_card_ids[0], __step.select_ordinal,
                        __candidateIds, out __pick, out __err))
                {
                    Digimon.Harness.InputDriver.Abort(
                        "MultipleSkills: " + __err + ". Candidates: " + DescribeScriptedCandidates());
                    return;
                }
                skillIndex = __pick;
            }
            else if (__step.select_value != int.MinValue)
            {
                if (__candidateIds == null)
                {
                    Digimon.Harness.InputDriver.Abort(
                        "MultipleSkills: select_value=" + __step.select_value + " cannot be " +
                        "range-checked -- this prompt's candidate list could not be computed " +
                        "(NOT MEASURED), and an unchecked index clears the trigger stack");
                    return;
                }
                if (__step.select_value < 0 || __step.select_value >= __candidateIds.Count)
                {
                    Digimon.Harness.InputDriver.Abort(
                        "MultipleSkills: select_value=" + __step.select_value + " is out of range " +
                        "for the " + __candidateIds.Count + " active effect(s) " +
                        DescribeScriptedCandidates() + ". DCGO would clear the whole trigger stack " +
                        "and play on without those triggers. Name the trigger by its source card " +
                        "with select_card_ids (+ select_ordinal when that card stacked more than " +
                        "one trigger), or use select_cancel to decline the stack");
                    return;
                }
                skillIndex = __step.select_value;
            }
            else
            {
                Digimon.Harness.InputDriver.Abort(
                    "MultipleSkills prompt needs select_card_ids (the trigger's source card, " +
                    "+ select_ordinal when that card stacked more than one trigger), " +
                    "select_value (a raw 0-based DCGO skill index) or select_cancel, got: " +
                    Digimon.Harness.SelectionAnswer.Describe(__step) +
                    ". Candidates: " + DescribeScriptedCandidates());
                return;
            }
        }

        // [Recording mod] trigger-order / multi-effect choice.
        Digimon.Recording.GameRecorder.Instance?.LogSelectionRow(
            playerID, "MultipleSkills", GManager.instance?.turnStateMachine?.gameContext?.TurnPhase.ToString() ?? "Unknown", intValue: skillIndex);

        Player selectionPlayer = GManager.instance.GetPlayerFromID(playerID);

        if (selectionPlayer == null)
        {
            return;
        }

        selectionPlayer.QueuePlayerSelection(new ValueSelection(skillIndex));
    }
}
