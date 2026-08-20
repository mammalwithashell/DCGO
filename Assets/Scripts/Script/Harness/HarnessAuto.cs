using UnityEngine;

namespace Digimon.Harness
{
    /// <summary>
    /// True when auto mode is driving BOTH seats, so the local seat's prompts
    /// must route to the AI path instead of waiting for a click nobody will make.
    /// </summary>
    /// <remarks>
    /// DCGO's auto mode was written for a bot opponent facing a human, so every
    /// decision point gates on `isYou` to choose "show UI and wait" over "let the
    /// AI decide". Under the recording harness both seats are bot-driven, and any
    /// gate that still routes the local seat to the UI path hangs the game.
    /// </remarks>
    public static class HarnessAuto
    {
        public static bool DrivesLocalSeat =>
            GManager.instance != null
            && GManager.instance.IsAI
            && GManager.instance.isAuto;
    }
}
