using Drova_Modding_API;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.AI.Logic;
using Il2CppDrova.AI.Modules;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Makes a remote player somebody a creature can choose to fight.
    ///
    /// Two corrections, unrelated in mechanism and identical in consequence - a teammate the creature
    /// standing next to them will not attack. Identity, in <see cref="GiveOwnIdentity"/>, which is the one
    /// that explains an enemy walking past the client toward a host across the map; and status, in
    /// <see cref="IsPlayerPostfix"/>.
    /// </summary>
    internal static class PuppetTargets
    {
        /// <summary>
        /// The key <c>PlayerAIActorFactory</c> gives every actor without an AI factory of its own.
        /// </summary>
        private const string SharedKey = "player";

        private static bool _reported;
        private static bool _reportedKey;

        /// <summary>
        /// How many times a remote player was allowed to count as a player to a creature's target chain.
        /// For <c>coop_status</c>.
        /// </summary>
        internal static int Claimed { get; private set; }

        /// <summary>
        /// How many remote bodies were given a target-list identity of their own.
        ///
        /// One per body, not one per call - the name is written onto the adapter once and stays. Zero with
        /// a body in the world is a fault: the collision is a property of the shipped prefabs rather than
        /// a possibility.
        /// </summary>
        internal static int Separated { get; private set; }

        /// <summary>
        /// Applies the patches. Called once at melon init.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPostfix(harmony, typeof(AIActorAdapter), nameof(AIActorAdapter.IsPlayer),
                typeof(PuppetTargets), nameof(IsPlayerPostfix));
        }

        /// <summary>Forgets the counts. Called when a session ends.</summary>
        internal static void Reset()
        {
            Claimed = 0;
            Separated = 0;
        }

        /// <summary>
        /// Gives a remote player's body its own name in every creature's target list.
        ///
        /// A puppet and the local player report the same one. <c>Player_Main.prefab</c> carries neither an
        /// <c>AI_ActorFactory</c> nor an <c>AI_FireflyFactory</c> - read out of the prefab, since
        /// <c>AIActorModule._actorFactory</c> is only ever assigned in an editor-only <c>OnValidate</c> and
        /// the code therefore cannot say - so <c>GetIAIFactory</c> falls through to
        /// <c>PlayerAIActorFactory</c>, which names its adapter <c>"player"</c>. A clone of that prefab gets
        /// the same name. Creatures each carry a factory and keep guid keys.
        ///
        /// <c>PathDistanceCheckActorDecorator</c> keys its whole target set by that string, so the two are
        /// one candidate: once a creature has banked the host, the client arriving with the same key keeps
        /// the host entry alive however far it has walked, and is itself refused as a duplicate. The list
        /// then holds the far host and nothing else, and it is the only thing the threat service chooses
        /// from.
        ///
        /// Written onto the adapter rather than patched. <c>Key</c> is read several times per creature per
        /// AI tick, so a postfix would be both a per-call cost and a per-call string conversion - and a
        /// Harmony patch on an IL2CPP method returning a string hands the postfix the raw
        /// <c>Il2CppString*</c>, which decoding as a managed string tears down the trampoline. One field
        /// write per body has none of those problems and outlives every call.
        ///
        /// Conditional on the collision being there, so a future build that gives the player prefab a
        /// factory turns this into a no-op rather than fighting it.
        ///
        /// Safe to change: <c>Key</c> has two consumers, this decorator and the AI's saved-position helper,
        /// which only behavior-tree tasks reach and a puppet's AI is deactivated. Worth noticing that under
        /// the shared key that helper would have written a puppet's position to <c>"playerX"</c>.
        /// </summary>
        internal static void GiveOwnIdentity(Actor puppet)
        {
            try
            {
                if (puppet == null) return;

                IAIActorFactory factory = puppet.AIModule?.GetIAIFactory()!;
                if (factory == null) return;

                AIActorAdapter? adapter = factory.GetActor()?.TryCast<AIActorAdapter>();
                if (adapter == null) return;

                if (!string.Equals(adapter._guidKey, SharedKey, StringComparison.Ordinal)) return;

                string? own = puppet.GetGuidComponent()?.GuidString;
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
        /// Exempts a remote player from the branch that gives up on a target.
        ///
        /// <c>TargetIsInCombat</c> drops a non-player from the creature's combat set three seconds after it
        /// stops perceiving them, and a puppet qualifies: <c>CheckIsPlayer</c> reads
        /// <c>CustomStats.IsPlayer</c>, which is a plain runtime property set by
        /// <c>PlayerInputController</c> on the actor it controls rather than anything a clone inherits. A
        /// real player standing in the same place is exempt.
        ///
        /// The adapter, not <c>CheckIsPlayer</c> underneath it, which would make a puppet the player to the
        /// crit roll, the save system and death handling too. <c>IAIActor.IsPlayer</c> has that one
        /// consumer in the whole game, and returns a bool - so unlike the key, it is safe to patch.
        /// </summary>
        private static void IsPlayerPostfix(AIActorAdapter __instance, ref bool __result)
        {
            try
            {
                if (__result) return;

                if (!CoopSession.IsLive) return;
                if (RemotePlayers.Count == 0) return;

                Actor actor = __instance._actor;
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
    }
}
