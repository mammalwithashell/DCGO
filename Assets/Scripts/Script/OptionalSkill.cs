using Photon;
using Photon.Pun;
using Photon.Realtime;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
public class OptionalSkill : MonoBehaviourPunCallbacks
{
    public string waitingText { get; set; } = "The opponent is considering whether to use the effect.";

    bool _useOptional = false;
    public IEnumerator SelectOptional(ICardEffect cardEffect, Hashtable hash)
    {
        List<string> _YesNoTexts = new List<string>() { "Use", "Not use" };

        Player player = cardEffect.EffectSourceCard.Owner;

        _useOptional = false;

        string _Message;

        List<Permanent> effectTargets = cardEffect.EffectTargets != null ? cardEffect.EffectTargets(hash) : null;

        if (effectTargets == null || effectTargets.Count == 0)
        {
            _Message = $"Will you use \"{cardEffect.EffectName}\"?";
        }
        else
        {
            _Message = $"Will you use \"{cardEffect.EffectName}\" targeting {string.Join(", ", effectTargets.Select(permanent => permanent.TopCard.CardNames[0]))}?";
        }


        #region ƒgƒ‰ƒbƒVƒ…‚ÌƒJ[ƒh‚ð•\Ž¦
        if (cardEffect != null)
        {
            if (cardEffect.EffectSourceCard != null)
            {
                if (cardEffect.EffectSourceCard.Owner.TrashCards.Contains(cardEffect.EffectSourceCard) || cardEffect.EffectSourceCard.Owner.LostCards.Contains(cardEffect.EffectSourceCard))
                {
                    if (cardEffect.EffectSourceCard.Owner.TrashHandCard != null)
                    {
                        if (!cardEffect.EffectSourceCard.Owner.TrashHandCard.gameObject.activeSelf)
                        {
                            cardEffect.EffectSourceCard.Owner.TrashHandCard.gameObject.SetActive(true);
                            cardEffect.EffectSourceCard.Owner.TrashHandCard.SetUpHandCard(cardEffect.EffectSourceCard);
                            cardEffect.EffectSourceCard.Owner.TrashHandCard.SetUpHandCardImage();
                            cardEffect.EffectSourceCard.Owner.TrashHandCard.OnOutline();
                            cardEffect.EffectSourceCard.Owner.TrashHandCard.SetBlueOutline();
                            cardEffect.EffectSourceCard.Owner.TrashHandCard.transform.localScale = new Vector3(1.4f, 1.4f, 1.4f);
                        }
                    }
                }
            }
        }
        #endregion

        // [Harness mod] Auto mode drives both seats now; without this, the
        // local seat's optional-skill Yes/No prompt would open UI and wait
        // for a click that never comes. Route it to the AI branch below.
        if (cardEffect.EffectSourceCard.Owner.isYou && !Digimon.Harness.HarnessAuto.DrivesLocalSeat)
        {
            Permanent permanent = cardEffect.EffectSourceCard.PermanentOfThisCard();
            List<FieldPermanentCard> highlightPermanents = new List<FieldPermanentCard>();

            if (permanent != null)
            {
                if (permanent.ShowingPermanentCard != null)
                {
                    highlightPermanents.Add(permanent.ShowingPermanentCard);
                
                }
            }

            if (effectTargets != null)
            {
                foreach (Permanent targetPermanent in effectTargets)
                {
                    if (targetPermanent.ShowingPermanentCard != null)
                    {
                        highlightPermanents.Add(targetPermanent.ShowingPermanentCard);
                    }
                }
            }

            if (highlightPermanents.Count > 0)
            {
                GManager.instance.hideCannotSelectObject.SetUpHideCannotSelectObject(highlightPermanents, false);
            }

            GManager.instance.commandText.OpenCommandText(_Message);

            List<Command_SelectCommand> commands = new List<Command_SelectCommand>()
            {
                new Command_SelectCommand(_YesNoTexts[0] ,() => photonView.RPC("SetUseOptional",RpcTarget.All, player.PlayerID, true),0),
            };

            GManager.instance.BackButton.OpenSelectCommandButton(_YesNoTexts[1], () => { photonView.RPC("SetUseOptional", RpcTarget.All, player.PlayerID, false); }, 0);

            GManager.instance.selectCommandPanel.SetUpCommandButton(commands);
        }

        else
        {
            bool ShowOpponentMessage = cardEffect.EffectDiscription.Contains("[Hand]") && GManager.instance.autoProcessing.executingMultipleSkills != null && !GManager.instance.autoProcessing.executingMultipleSkills.IsOnlyHandEffectStacked;

            if (ShowOpponentMessage)
            {
                GManager.instance.commandText.OpenCommandText(waitingText);
            }

            if (GManager.instance.IsAI)
            {
                SetUseOptional(player.PlayerID, RandomUtility.IsSucceedProbability(0.9f));
            }
        }

        yield return new WaitUntil(() => player.HasPlayerSelection());
        ValueSelection valueSelection = player.DequeuePlayerSelection<ValueSelection>();
        _useOptional = valueSelection != null ? valueSelection.ValueAsBool() : false;

        GManager.instance.selectCommandPanel.Off();

        GManager.instance.BackButton.CloseSelectCommandButton();
        GManager.instance.hideCannotSelectObject.Close();

        GManager.instance.commandText.CloseCommandText();
        yield return new WaitWhile(() => GManager.instance.commandText.gameObject.activeSelf);

        cardEffect.SetUseOptional(_useOptional);

        cardEffect.EffectSourceCard.Owner.TrashHandCard.gameObject.SetActive(false);
    }

    [PunRPC]
    public void SetUseOptional(int playerID, bool useOptional)
    {
        // [Harness mod - phase 2] A scripted line answers here, before the
        // recorder sees anything, so the recorded row carries what the script
        // asked for rather than the value the AI computed and we discard.
        // A false return is never "the script declined" -- TryAnswerStep has
        // already aborted the job on a mismatch -- so do not fall through.
        //
        // This is the yes/no every "you may" clause funnels through, so it is
        // the single most load-bearing site for a clause exam: it is what lets
        // a scenario FORCE an optional effect to execute rather than hoping
        // the AI accepted it.
        if (Digimon.Harness.InputDriver.IsActive)
        {
            Digimon.Harness.HarnessJobStep __step;
            if (!Digimon.Harness.InputDriver.TryAnswerStep(
                    playerID, Digimon.Harness.InputDriver.KindOptionalSkill,
                    1, null, out __step))
            {
                return;
            }
            // select_bool is only an answer when select_has_bool marks it
            // present -- a bare bool cannot distinguish "no" from "absent",
            // and a step that FORGOT the answer must abort loudly rather than
            // silently decline the optional effect under test.
            //
            // select_cancel is ALSO an answer here, and it means "no". An
            // OptionalSkill prompt IS a yes/no, so declining it and answering
            // no are the same choice -- unlike the Select*Effect prompts,
            // where cancel is a distinct "walk away from the pick" shape.
            // Without this mapping every scenario `decline:` aimed at a "you
            // may" gate aborted the job (review finding 1: the scenario
            // schema serializes decline as select_cancel, and this hook
            // accepted only select_bool -- 6 scenarios unauthorable).
            if (__step.select_cancel)
            {
                useOptional = false;
            }
            else if (!__step.select_has_bool)
            {
                Digimon.Harness.InputDriver.Abort(
                    "OptionalSkill prompt needs select_bool (with select_has_bool: true) or select_cancel, got: " +
                    Digimon.Harness.SelectionAnswer.Describe(__step));
                return;
            }
            else
            {
                useOptional = __step.select_bool;
            }
        }

        // [Recording mod] canonical optional-effect yes/no.
        Digimon.Recording.GameRecorder.Instance?.LogSelectionRow(
            playerID, "OptionalSkill", GManager.instance?.turnStateMachine?.gameContext?.TurnPhase.ToString() ?? "Unknown", boolValue: useOptional);

        Player selectionPlayer = GManager.instance.GetPlayerFromID(playerID);

        if (selectionPlayer == null)
        {
            return;
        }

        selectionPlayer.QueuePlayerSelection(new ValueSelection(useOptional));
    }
}
