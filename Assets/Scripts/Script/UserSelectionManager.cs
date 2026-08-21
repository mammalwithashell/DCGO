using Photon.Pun;
using System;
using System.Collections.Generic;
using System.Collections;
using UnityEngine;
using System.Linq;

public class UserSelectionManager : MonoBehaviourPunCallbacks
{
    bool _endSelect = false;
    int _selectedIntValue = 0;
    bool _isLocal = false;
    bool _selectedBoolValue => getBoolFromInt(_selectedIntValue);
    public int SelectedIntValue => _selectedIntValue;
    public bool SelectedBoolValue => _selectedBoolValue;

    Player _selectPlayer;

    [PunRPC]
    public void SetIntForPlayer(int playerID, int value)
    {
        Player selectionPlayer = GManager.instance.GetPlayerFromID(playerID);

        if (selectionPlayer == null)
        {
            return;
        }

        // [Harness mod - phase 2] A scripted line answers here, before the
        // recorder sees anything, so the recorded row carries what the script
        // asked for rather than the value the AI computed and we discard.
        // A false return is never "the script declined" -- TryAnswer has
        // already aborted the job on a mismatch -- so do not fall through.
        //
        // This is the FALLBACK channel, not "every selection response": every
        // typed prompt has its own dedicated RPC hook. Its rows carry no
        // candidate list and no per-prompt meaning, so a scripted step here
        // asserts nothing beyond the kind, and its value has to be authored
        // from a recording of the same position.
        if (Digimon.Harness.InputDriver.IsActive)
        {
            int __scripted;
            if (!Digimon.Harness.InputDriver.TryAnswer(
                    playerID, Digimon.Harness.InputDriver.KindGenericInt,
                    -1, null, out __scripted))
            {
                return;
            }
            value = __scripted;
        }

        // [Recording mod] capture the selection. Hooked at the [PunRPC] target
        // so direct, RPC-wrapped, and bot-Random paths all route through here.
        // Phase determination uses GameContext.TurnPhase from the global manager.
        Digimon.Recording.GameRecorder.Instance?.LogSelectionRow(
            playerID, "generic_int",
            GManager.instance?.turnStateMachine?.gameContext?.TurnPhase.ToString() ?? "Unknown",
            intValue: value);

        selectionPlayer.QueuePlayerSelection(new ValueSelection(value));
    }

    public void SetInt(int value)
    {
        _selectedIntValue = value;
        _endSelect = true;
    }

    protected void SetInt_RPC(int playerID, int value)
    {
        photonView.RPC("SetIntForPlayer", RpcTarget.All, playerID, value);
    }

    [PunRPC]
    public void SetBoolForPlayer(int playerID, bool value)
    {
        Player selectionPlayer = GManager.instance.GetPlayerFromID(playerID);

        if (selectionPlayer == null)
        {
            return;
        }

        // [Harness mod - phase 2] Scripted answer; see the SetIntForPlayer
        // twin above for why this fallback channel asserts less than a typed
        // prompt does.
        if (Digimon.Harness.InputDriver.IsActive)
        {
            int __scripted;
            if (!Digimon.Harness.InputDriver.TryAnswer(
                    playerID, Digimon.Harness.InputDriver.KindGenericBool,
                    1, null, out __scripted))
            {
                return;
            }
            value = __scripted != 0;
        }

        // [Recording mod] capture bool selection (yes/no, optional triggers).
        Digimon.Recording.GameRecorder.Instance?.LogSelectionRow(
            playerID, "generic_bool",
            GManager.instance?.turnStateMachine?.gameContext?.TurnPhase.ToString() ?? "Unknown",
            boolValue: value);

        selectionPlayer.QueuePlayerSelection(new ValueSelection(value));
    }

    public void SetBool(bool value)
    {
        _selectedIntValue = getIntFromBool(value);
        _endSelect = true;
    }

    protected void SetBool_RPC(int playerID, bool value)
    {
        photonView.RPC("SetBoolForPlayer", RpcTarget.All, playerID, value);
    }

    internal int getIntFromBool(bool value)
    {
        return value ? 1 : 0;
    }

    internal bool getBoolFromInt(int value)
    {
        return value != 0;
    }

    public IEnumerator WaitForEndSelect()
    {
        if (_selectPlayer != null)
        {
            yield return new WaitUntil(() => _selectPlayer.HasPlayerSelection());

            ValueSelection valueSeletion = _selectPlayer.DequeuePlayerSelection<ValueSelection>();

            if (valueSeletion != null)
            {
                _selectedIntValue = valueSeletion.ValueAsInt();
            }
        }
        else
        {
            yield return new WaitWhile(() => !_endSelect);
        }

        _endSelect = false;
        _selectPlayer = null;

        GManager.instance.commandText.CloseCommandText();
        yield return new WaitWhile(() => GManager.instance.commandText.gameObject.activeSelf);
    }

    public void SetIntSelection(List<SelectionElement<int>> selectionElements, Player selectPlayer, string selectPlayerMessage, string notSelectPlayerMessage, bool IsLocal = false)
    {
        _endSelect = false;
        _selectedIntValue = 0;
        _selectPlayer = selectPlayer;
        _isLocal = IsLocal;

        // [Harness mod] This is DCGO's GENERIC int/bool prompt -- the channel
        // every "you may X" and multi-choice question funnels through. Under
        // auto mode the local seat is bot-driven, so routing it to the human
        // command-button path opens a prompt nothing will ever click. The else
        // branch below is the AI's auto-answer.
        if (selectPlayer.isYou && !Digimon.Harness.HarnessAuto.DrivesLocalSeat)
        {
            GManager.instance.commandText.OpenCommandText(selectPlayerMessage);

            List<Command_SelectCommand> command_SelectCommands = new List<Command_SelectCommand>();

            foreach (SelectionElement<int> selectionElement in selectionElements)
            {
                command_SelectCommands.Add(new Command_SelectCommand(selectionElement.Message, () => SendSelection(selectionElement.Value), selectionElement.SpriteIndex));
            }

            GManager.instance.selectCommandPanel.SetUpCommandButton(command_SelectCommands);
        }

        else
        {
            GManager.instance.commandText.OpenCommandText(notSelectPlayerMessage);

            #region AI���[�h
            if (GManager.instance.IsAI)
            {
                List<int> canSelectValue = new List<int>();

                foreach (SelectionElement<int> selectionElement in selectionElements)
                {
                    canSelectValue.Add(selectionElement.Value);
                }

                int value = canSelectValue.Count >= 1 ? canSelectValue[UnityEngine.Random.Range(0, canSelectValue.Count)] : 0;
                SendSelection(value);
            }
            #endregion
        }

        void SendSelection(int value)
        {
            if (_isLocal)
            {
                SetIntForPlayer(selectPlayer.PlayerID, value);
            }
            else
            {
                SetInt_RPC(selectPlayer.PlayerID, value);
            }
        }
    }

    public void SetBoolSelection(List<SelectionElement<bool>> selectionElements, Player selectPlayer, string selectPlayerMessage, string notSelectPlayerMessage, bool IsLocal = false)
    {
        _endSelect = false;
        _selectedIntValue = 0;
        _selectPlayer = selectPlayer;
        _isLocal = IsLocal;

        // [Harness mod] This is DCGO's GENERIC int/bool prompt -- the channel
        // every "you may X" and multi-choice question funnels through. Under
        // auto mode the local seat is bot-driven, so routing it to the human
        // command-button path opens a prompt nothing will ever click. The else
        // branch below is the AI's auto-answer.
        if (selectPlayer.isYou && !Digimon.Harness.HarnessAuto.DrivesLocalSeat)
        {
            GManager.instance.commandText.OpenCommandText(selectPlayerMessage);

            List<Command_SelectCommand> command_SelectCommands = new List<Command_SelectCommand>();

            foreach (SelectionElement<bool> selectionElement in selectionElements)
            {
                command_SelectCommands.Add(new Command_SelectCommand(selectionElement.Message, () => SendSelection(selectionElement.Value), selectionElement.SpriteIndex));
            }

            GManager.instance.selectCommandPanel.SetUpCommandButton(command_SelectCommands);
        }

        else
        {
            GManager.instance.commandText.OpenCommandText(notSelectPlayerMessage);

            #region AI���[�h
            if (GManager.instance.IsAI)
            {
                List<bool> canSelectValue = new List<bool>();

                foreach (SelectionElement<bool> selectionElement in selectionElements)
                {
                    canSelectValue.Add(selectionElement.Value);
                }

                bool value = canSelectValue.Count >= 1 ? canSelectValue[UnityEngine.Random.Range(0, canSelectValue.Count)] : false;
                SendSelection(value);
            }
            #endregion
        }

        void SendSelection(bool value)
        {
            if (_isLocal)
            {
                SetBoolForPlayer(selectPlayer.PlayerID, value);
            }
            else
            {
                SetBool_RPC(selectPlayer.PlayerID, value);
            }
        }
    }
}

public class SelectionElement<T>
{
    public SelectionElement(string message, T value, int spriteIndex)
    {
        this.Message = message;
        this.Value = value;
        this.SpriteIndex = spriteIndex;
    }
    public string Message { get; private set; }
    public T Value { get; private set; }
    public int SpriteIndex { get; private set; }
}