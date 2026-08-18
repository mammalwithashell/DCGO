using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Digimon.Harness
{
    /// <summary>
    /// Builds a <see cref="DeckData"/> from card ID strings.
    /// </summary>
    /// <remarks>
    /// Jobs carry card IDs ("EX12-035"), never DCGO deck codes. The deck code is
    /// a base-n encoding over DCGO's internal <c>CEntity_Base.CardIndex</c>, so
    /// reimplementing it host-side would duplicate a table that rots whenever
    /// DCGO re-indexes. Resolving here keeps the encoding owned by the codebase
    /// that defines it.
    ///
    /// Digitama cards are separated by kind rather than by a second list, so the
    /// job can ship one flat card-ID array per seat.
    /// </remarks>
    public static class DeckBuilder
    {
        public static DeckData FromCardIds(string deckName, string[] cardIds)
        {
            if (cardIds == null || cardIds.Length == 0)
            {
                Debug.LogError("[Harness] deck '" + deckName + "' has no cards");
                return null;
            }

            var main = new List<CEntity_Base>();
            var digitama = new List<CEntity_Base>();

            foreach (string id in cardIds)
            {
                CEntity_Base entity = ContinuousController.instance.SortedCardList
                    .FirstOrDefault(e => e.CardID == id);
                if (entity == null)
                {
                    Debug.LogError("[Harness] unknown card id '" + id + "' in deck '" + deckName + "'");
                    return null;
                }

                // No CEntity_Base.IsDigitama() helper exists; DCGO itself
                // distinguishes digitama cards this way in
                // CardObjectController.CreatePlayerDecks.
                if (entity.cardKind.Contains(CardKind.DigiEgg))
                {
                    digitama.Add(entity);
                }
                else
                {
                    main.Add(entity);
                }
            }

            string code = DeckData.GetDeckCode(deckName, main, digitama, null);
            if (!DeckData.IsValidDeckCode(code))
            {
                Debug.LogError("[Harness] deck '" + deckName + "' produced an invalid deck code");
                return null;
            }
            return new DeckData(code);
        }
    }
}
