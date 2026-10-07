using Drova_Modding_API;
using DrovaCoopMod.Sync;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Lets a replayed story variable move this world but not this player's body: the story's inventory swap is skipped.
    /// </summary>
    internal static class StoryEffectGate
    {
        /// <summary>How many player-body effects were refused. For <c>coop_status</c>.</summary>
        internal static int Suppressed { get; private set; }

        /// <summary>Whether the prefixes are in place.</summary>
        internal static bool Installed { get; private set; }

        /// <summary>Forgets the count when a session ends.</summary>
        internal static void Reset()
        {
            Suppressed = 0;
        }

        /// <summary>Applies the prefixes. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            int applied = 0;

            if (Hooking.TryPrefix(harmony, typeof(Il2CppDrova.GlobalVarSystem.GlobalObserverBhvr_ChangePlayerInventory),
                "ConditionChangedListener", typeof(StoryEffectGate), nameof(SkipWhileSuppressed)))
            {
                applied++;
            }

            Installed = applied > 0;

            // Logged either way: the GVar apply path relies on this gate holding.
            if (applied == 1)
            {
                MelonLogger.Msg("[GVar] player-body effect gate installed on 1 observer; a teammate's " +
                    "story can move this world without moving this player.");
            }
            else
            {
                MelonLogger.Warning($"[GVar] player-body effect gate installed on {applied} of 1 " +
                    "observers. Replicated story variables may act on this player directly. Run " +
                    "coop_status to see how often the gate fires.");
            }
        }

        private static bool SkipWhileSuppressed()
        {
            if (!RemoteApply<StoryScope>.InProgress) return true;

            Suppressed++;
            return false;
        }
    }

    /// <summary>A replicated variable's change event being replayed here.</summary>
    internal readonly struct StoryScope : IApplyScope
    {
    }
}
