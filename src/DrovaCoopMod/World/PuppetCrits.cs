using Drova_Modding_API;
using Il2Cpp;
using Il2CppDrova;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Stops a remote player's body rolling its own critical hits.
    ///
    /// **A puppet reads as the player to the crit roll.** <c>CritUtil.CalcCritData</c> only rolls when
    /// <c>EntityGameHandler.CheckIsPlayer(source)</c> is true, which reads <c>CustomStats.IsPlayer</c> -
    /// and a body cloned from the player prefab carries it. So a puppet swinging at an NPC rolls a crit
    /// of its own, out of this machine's random stream, with no relation to what its owner's machine
    /// rolled for the same swing. The two disagree about the figure, and neither is more right than the
    /// other.
    ///
    /// Refusing the roll is what makes the blow deterministic, which is the property that matters while
    /// enemy health is still simulated on both machines: a swing that does the same thing everywhere can
    /// be reconciled later, and a swing that rolls dice separately on every machine cannot. It is also
    /// what the owner's own game already accounts for, since the crit that counted was rolled there.
    ///
    /// **Only the puppet's own attacks.** The blows this mod applies are unaffected either way -
    /// <see cref="RemoteDamage"/> builds its health change with a null source, and a null source fails
    /// <c>CheckIsPlayer</c> outright, so nothing arriving over the wire can re-roll a settled crit.
    /// </summary>
    internal static class PuppetCrits
    {
        // Counted rather than announced per roll. This runs inside every damage calculation in the game,
        // and the thing worth knowing is whether the patch fires at all - a public static method is
        // exactly the shape the AOT compiler inlines, and a patch that applies and never runs looks
        // identical to one that is working.
        private static int _suppressed;

        /// <summary>
        /// Applies the patch. Called once at melon init.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPostfix(harmony, typeof(CritUtil), nameof(CritUtil.CalcCritData),
                typeof(PuppetCrits), nameof(CalcCritDataPostfix));
        }

        /// <summary>
        /// Turns a puppet's crit back into an ordinary hit.
        ///
        /// A postfix rather than a prefix that skips: the roll is cheap, and letting it happen means this
        /// only has to answer the question the game already answered rather than reproduce the branch it
        /// took to answer it.
        /// </summary>
        private static void CalcCritDataPostfix(Entity source, ref CritData __result)
        {
            try
            {
                if (__result == null || __result.GetCritType() != CritData.CritType.Crit) return;
                if (source == null || !RemotePlayers.IsPuppet(source)) return;

                // Rebuilt rather than edited, so what comes out is exactly what the game itself produces
                // for a source it does not regard as a player: no crit, and a factor of one.
                __result = new CritData(CritData.CritType.None, 1f);

                _suppressed++;

                // The first one only. That the patch runs at all is the fact worth having in a log; how
                // often a remote body would have crit is not.
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
