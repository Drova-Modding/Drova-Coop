using Drova_Modding_API;
using DrovaCoopMod.Systems.Enemies;
using DrovaCoopMod.Systems.Players.Patches;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using Il2CppDrova.AI.Logic;
using Il2CppDrova.AI.Modules;
using MelonLoader;

namespace DrovaCoopMod.Systems.Players
{
    /// <summary>
    /// Makes a remote player somebody a creature can choose to fight, by target-list identity and player status.
    /// </summary>
    internal sealed class PuppetTargets : ICoopSystem
    {
        /// <summary>The key <c>PlayerAIActorFactory</c> gives every actor without an AI factory of its own.</summary>
        private const string SharedKey = "player";

        private static bool _reported;
        private static bool _reportedKey;

        /// <summary>How many times a remote player counted as a player to a creature's target chain. For <c>coop_status</c>.</summary>
        internal static int Claimed { get; private set; }

        /// <summary>How many remote bodies got their own target-list identity. Zero with a body in the world is a fault.</summary>
        internal static int Separated { get; private set; }

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPostfix(harmony, typeof(AIActorAdapter), nameof(AIActorAdapter.IsPlayer),
                typeof(PuppetTargetsPatches), nameof(PuppetTargetsPatches.IsPlayerPostfix));
        }

        /// <summary>Forgets the counts.</summary>
        private static void Reset()
        {
            Claimed = 0;
            Separated = 0;
        }

        /// <summary>
        /// Gives a remote player's body its own key in every creature's target list.
        /// A player-prefab clone gets the adapter key <c>"player"</c>, and <c>PathDistanceCheckActorDecorator</c>
        /// keys targets by it, so host and puppet would be one candidate.
        /// </summary>
        internal static void GiveOwnIdentity(Actor puppet)
        {
            try
            {
                if (puppet == null) return;

                IAIActorFactory? factory = puppet.AIModule?.GetIAIFactory();
                if (factory == null) return;

                AIActorAdapter? adapter = factory.GetActor()?.TryCast<AIActorAdapter>();
                if (adapter == null) return;

                if (!string.Equals(adapter._guidKey, SharedKey, StringComparison.Ordinal)) return;

                string own = EnemyRegistry.GuidOf(puppet);
                if (string.IsNullOrEmpty(own)) return;

                adapter._guidKey = own;
                Separated++;

                if (!_reportedKey)
                {
                    _reportedKey = true;

                    MelonLogger.Msg("[Enemy] a remote player's body shared the local player's target-list " +
                        "identity and has been given its own. Until now a creature that had chosen either " +
                        "of them could never be handed the other.");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] naming a remote player for the target list failed: " + e);
            }
        }

        /// <summary>
        /// Keeps <c>TargetIsInCombat</c> from dropping a remote player three seconds after losing sight of them.
        /// Patched on the adapter, not <c>CheckIsPlayer</c>, which the crit roll, saves and death handling also read.
        /// </summary>
        internal static void IsPlayerPostfix(AIActorAdapter __instance, ref bool __result)
        {
            try
            {
                if (__result) return;

                if (!CoopSession.IsLive) return;
                if (RemotePlayers.Count == 0) return;

                Actor? actor = __instance._actor;
                if (actor == null) return;

                if (!RemotePlayers.IsPuppet(actor)) return;

                __result = true;
                Claimed++;

                if (!_reported)
                {
                    _reported = true;

                    MelonLogger.Msg("[Enemy] a remote player now counts as a player to the AI. Without this a " +
                        "creature gives up on them three seconds after losing sight of them, which a real " +
                        "player standing in the same place never has happen.");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] deciding whether a target is a player failed: " + e);
            }
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (!loaded) Reset();
        }
    }
}
