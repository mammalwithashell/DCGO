using System;
using UnityEngine;

namespace Digimon.Harness
{
    /// <summary>
    /// One unattended game, as written by the `dcgo-harness submit` CLI.
    /// </summary>
    /// <remarks>
    /// Parsed with Unity's <see cref="JsonUtility"/>, which handles nested
    /// [Serializable] classes and arrays and silently ignores unknown fields —
    /// exactly the forward-compatibility we want, since phase-2 jobs will carry
    /// `deck_order` and `inputs` that a phase-1 client must tolerate.
    ///
    /// Field names are snake_case to match the JSON verbatim; JsonUtility has no
    /// name-mapping attribute, so the C# fields wear the wire names.
    /// </remarks>
    [Serializable]
    public class HarnessJob
    {
        public string job_id;
        public string policy;
        public HarnessJobDecks decks;
        public int first_player;
        public long seed;
        public HarnessJobLimits limits;

        /// <summary>Parse a job file. Returns null when the text is unusable.</summary>
        public static HarnessJob Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                HarnessJob job = JsonUtility.FromJson<HarnessJob>(json);
                if (job == null || string.IsNullOrEmpty(job.job_id)) return null;
                if (job.decks == null || job.decks.p0 == null || job.decks.p1 == null) return null;
                if (job.limits == null) job.limits = new HarnessJobLimits();
                return job;
            }
            catch (Exception e)
            {
                Debug.LogError("[Harness] job parse failed: " + e.Message);
                return null;
            }
        }
    }

    [Serializable]
    public class HarnessJobDecks
    {
        public string[] p0;
        public string[] p1;
    }

    [Serializable]
    public class HarnessJobLimits
    {
        public int max_turns = 40;
        public int timeout_seconds = 180;
    }
}
