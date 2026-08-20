using UnityEditor;
using UnityEngine;

namespace Digimon.Harness.EditorTools
{
    /// <summary>
    /// Editor toggle for the unattended job harness.
    /// </summary>
    /// <remarks>
    /// <see cref="HarnessConfig.Enabled"/> defaults to false on purpose: a stale
    /// job file must never hijack a normal play session. That leaves the
    /// question of how to turn it ON for a real batch. Editing the default in
    /// source would commit exactly the hazard the default exists to prevent, and
    /// an environment variable would need Unity relaunched to pick up.
    ///
    /// A menu toggle is explicit, discoverable, and takes effect on the next
    /// Play without a restart. The choice is stored in EditorPrefs so it
    /// survives domain reloads and editor restarts, and is re-applied to
    /// <see cref="HarnessConfig"/> before every play-mode entry — a static
    /// field alone would be reset by the domain reload that precedes Play.
    /// </remarks>
    [InitializeOnLoad]
    public static class HarnessMenu
    {
        private const string MenuPath = "Digimon/Harness/Enabled";
        private const string PrefKey = "Digimon.Harness.Enabled";

        static HarnessMenu()
        {
            // Re-apply on every domain reload, and again as play mode starts,
            // so the runtime bootstrap sees the operator's choice.
            Apply();
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode)
                {
                    Apply();
                }
            };
        }

        private static void Apply()
        {
            HarnessConfig.Enabled = EditorPrefs.GetBool(PrefKey, false);
        }

        [MenuItem(MenuPath, false, 100)]
        private static void Toggle()
        {
            bool next = !EditorPrefs.GetBool(PrefKey, false);
            EditorPrefs.SetBool(PrefKey, next);
            HarnessConfig.Enabled = next;

            Debug.Log(next
                ? "[Harness] ENABLED. The next Play will claim jobs from " + HarnessConfig.JobsDir
                : "[Harness] disabled. Play behaves as normal DCGO.");
        }

        [MenuItem(MenuPath, true)]
        private static bool ToggleValidate()
        {
            Menu.SetChecked(MenuPath, EditorPrefs.GetBool(PrefKey, false));
            return true;
        }

        /// <summary>
        /// Prints where the harness is looking, so an operator can confirm the
        /// Unity side and the CLI side agree on a root before a batch.
        /// </summary>
        [MenuItem("Digimon/Harness/Log job directory", false, 101)]
        private static void LogRoot()
        {
            Debug.Log("[Harness] root=" + HarnessConfig.Root
                      + "\n  jobs=" + HarnessConfig.JobsDir
                      + "\n  enabled=" + EditorPrefs.GetBool(PrefKey, false));
        }
    }
}
