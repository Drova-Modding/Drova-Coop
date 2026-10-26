using Drova_Modding_API;
using DrovaCoopMod.Systems.Players.Patches;
using Il2CppDrova.HitSystem;
using MelonLoader;

namespace DrovaCoopMod.Systems.Players
{
    /// <summary>
    /// Keeps teammates out of the player's combat state. The player's own volume raises it, and the combat music, for any
    /// hostile body inside, and friendly fire makes every teammate one, so two players standing together heard battle.
    /// </summary>
    internal sealed class PuppetCombatState : ICoopSystem
    {
        string? ICoopSystem.HookSwitch => HookSwitches.PuppetCombat;

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPrefix(harmony, typeof(HitReceiveBhvr_CombatBhvrs), nameof(HitReceiveBhvr_CombatBhvrs.OnEnter),
                typeof(PuppetCombatStatePatches), nameof(PuppetCombatStatePatches.EnterPrefix));
            Hooking.TryPrefix(harmony, typeof(HitReceiveBhvr_CombatBhvrs), nameof(HitReceiveBhvr_CombatBhvrs.OnHit),
                typeof(PuppetCombatStatePatches), nameof(PuppetCombatStatePatches.EnterPrefix));
        }

        /// <summary>
        /// Refuses a teammate's body entering a player's combat volume, and anyone entering the one a teammate's body
        /// carries, which plays its music here too.
        /// </summary>
        internal static bool EnterPrefix(HitReceiveBhvr_CombatBhvrs __instance, HitTriggerArgs? args)
        {
            try
            {
                return args == null || (!RemotePlayers.IsPuppet(args.Entity) && !RemotePlayers.IsPuppet(__instance._actor));
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Puppet] judging a combat volume entry failed: " + e);
                return true;
            }
        }
    }
}
