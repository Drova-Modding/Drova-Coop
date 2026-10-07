using Drova_Modding_API;
using Il2Cpp;
using Il2CppDrova;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Stops a remote player's body rolling its own critical hits.
    /// A puppet carries <c>CustomStats.IsPlayer</c>, so <c>CritUtil.CalcCritData</c> would roll for it,
    /// separately from the crit its owner's machine already rolled.
    /// </summary>
    internal static class PuppetCrits
    {
        private static int _suppressed;

        /// <summary>Applies the patch. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPostfix(harmony, typeof(CritUtil), nameof(CritUtil.CalcCritData),
                typeof(PuppetCrits), nameof(CalcCritDataPostfix));
        }

        /// <summary>Turns a puppet's crit back into an ordinary hit.</summary>
        private static void CalcCritDataPostfix(Entity source, ref CritData __result)
        {
            try
            {
                if (__result == null || __result.GetCritType() != CritData.CritType.Crit) return;
                if (source == null || !RemotePlayers.IsPuppet(source)) return;

                // What the game produces for a non-player source.
                __result = new CritData(CritData.CritType.None, 1f);

                _suppressed++;

                if (_suppressed == 1)
                {
                    MelonLogger.Msg("[Crit] a remote player's body rolled a critical hit of its own; refused. " +
                        "Its owner's machine already decided whether that swing crit.");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Crit] refusing a remote body's critical hit failed: " + e);
            }
        }
    }
}
