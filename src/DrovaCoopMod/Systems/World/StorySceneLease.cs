using Drova_Modding_API;
using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Enemies;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.Systems.World.Patches;
using Il2CppDrova;
using Il2CppDrova.GlobalVarSystem;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.World
{
    /// <summary>
    /// Runs a world-started story scene (forced dialogue, wave fight) only on a machine whose player is at it.
    /// While only a teammate is there, the observer is held and the follow watch brings this player along.
    /// </summary>
    internal sealed class StorySceneLease : ICoopSystem
    {
        // The follow watch's own distance, so a player brought along always lands inside it.
        private const float SceneReach = 3000f;

        /// <summary>The game's own notice radius for a walk-up talk (ActorIntentBhvr.SeeRadiusClose).</summary>
        private const float IntentDeferRange = 70f;

        string? ICoopSystem.HookSwitch => HookSwitches.Lease;

        /// <summary>How many frames a dialogue or wave observer was held for a teammate's scene. For <c>coop_status</c>.</summary>
        internal static int Held { get; private set; }

        /// <summary>Whether the patch applied.</summary>
        internal static bool Hooked { get; private set; }

        /// <summary>How many walk-up talks a client left to a nearer teammate. For the log.</summary>
        private static int IntentsDeferred { get; set; }

        /// <summary>How many walk-up talks the host left to a teammate already in them. For the log.</summary>
        private static int HostIntentsDeferred { get; set; }

        /// <summary>Forgets the count.</summary>
        private static void Reset()
        {
            Held = 0;
        }

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            Hooked = Hooking.TryPrefix(harmony, typeof(GlobalObserverBhvr_StartDialogue), "Update",
                typeof(StorySceneLeasePatches), nameof(StorySceneLeasePatches.UpdatePrefix));

            // The listener, because TriggerDialogue below it may be inlined.
            Hooking.TryPrefix(harmony, typeof(ActorIntentBhvr), "UpdateCognitionListener",
                typeof(StorySceneLeasePatches), nameof(StorySceneLeasePatches.IntentPrefix));

            Hooked &= Hooking.TryPrefix(harmony, typeof(GlobalObserverBhvr_WavesSpawning), "ManagedUpdate",
                typeof(StorySceneLeasePatches), nameof(StorySceneLeasePatches.WavesPrefix));

            if (!Hooked)
            {
                MelonLogger.Warning("[Lease] the story-dialogue or wave observer could not be patched; a " +
                    "teammate's world dialogue may start for this player wherever they are, or a wave fight " +
                    "may run where only one of you is.");
            }
        }

        // A walk-up talk runs in one world only, so both players near an NPC do not start it twice.
        internal static bool IntentPrefix(ActorIntentBhvr __instance)
        {
            try
            {
                // Host: leave the talk to a teammate already in it (EnemyEngagements) or the only one near.
                if (CoopSession.State == CoopSessionState.Hosting)
                {
                    Actor? held = __instance._actor;
                    if (held == null) return true;

                    if (PlayerAccess.TryGetPlayer(out Actor me)
                        && (me.GetFeetPosition() - held.GetFeetPosition()).sqrMagnitude > IntentDeferRange * IntentDeferRange
                        && RemotePlayers.IsAnyWithin(held.GetFeetPosition(), IntentDeferRange))
                    {
                        if (HostIntentsDeferred++ == 0)
                        {
                            MelonLogger.Msg("[Lease] an NPC's walk-up talk was left to the teammate standing by it.");
                        }

                        return false;
                    }

                    if (!EnemyRegistry.TryGetSessionId(held, out EnemyNetId heldId) || !EnemyEngagements.IsHeld(heldId)) return true;

                    if (HostIntentsDeferred++ == 0)
                    {
                        MelonLogger.Msg("[Lease] an NPC's walk-up talk was left to the teammate already talking to it.");
                    }

                    return false;
                }

                if (CoopSession.State != CoopSessionState.Joined) return true;

                Actor? npc = __instance._actor;
                if (npc == null) return true;

                // Client: leave it to any player inside the notice radius and nearer the NPC, lower id on a tie.
                Vector2 at = npc.GetFeetPosition();
                float selfSq = PlayerAccess.TryGetPlayer(out Actor self)
                    ? (self.GetFeetPosition() - at).sqrMagnitude
                    : float.MaxValue;
                byte selfId = CoopSession.LocalPlayerId.Value;

                foreach (KeyValuePair<PlayerId, RemotePlayer> other in RemotePlayers.Bodies)
                {
                    if (other.Value.Body == null) continue;

                    float otherSq = (other.Value.Body.GetFeetPosition() - at).sqrMagnitude;
                    if (otherSq > IntentDeferRange * IntentDeferRange) continue;
                    if (otherSq > selfSq || (otherSq == selfSq && other.Key.Value > selfId)) continue;

                    if (IntentsDeferred++ == 0)
                    {
                        MelonLogger.Msg("[Lease] an NPC's walk-up talk was left to a teammate standing nearer it.");
                    }

                    return false;
                }

                return true;
            }
            catch
            {
                return true;
            }
        }

        internal static bool UpdatePrefix(GlobalObserverBhvr_StartDialogue __instance)
        {
            try
            {
                // Wait for the speaker's body: the start uses its owner unchecked and throws after marking itself
                // started. Only while a living lazy actor can still build that body.
                EntityInfo? owner = __instance._dialogueOwner;
                if (!__instance._startState && owner != null)
                {
                    Actor? body = owner.GetActorFromInfo();
                    Il2CppDrova.Utilities.LazyLoading.LazyActor? lazy = body == null ? owner.GetLazyActorFromInfo() : null;
                    bool building = body == null ? lazy != null && !lazy.IsDead() : !body.IsInitialized;
                    if (building && __instance._condition != null && __instance._condition.CheckCondition()) return false;
                }
            }
            catch
            {
                // The wait is a guard only. Failing it leaves the game's own start.
            }

            if (RemotePlayers.Count == 0) return true;

            try
            {
                // Only a dialogue about to start, or the follow would fire for every unfired observer.
                if (__instance._startState) return true;
                if (!IsTeammateAtSceneAlone(ScenePosition(__instance))) return true;
                if (__instance._condition == null || !__instance._condition.CheckCondition()) return true;

                return !Hold();
            }
            catch
            {
                return true;
            }
        }

        internal static bool WavesPrefix(GlobalObserverBhvr_WavesSpawning __instance)
        {
            if (RemotePlayers.Count == 0) return true;

            try
            {
                // Past the last wave the observer stays registered until its condition drops.
                if (__instance._currentWaveindex >= __instance._waves.Count) return true;
                if (!IsTeammateAtSceneAlone(WavePosition(__instance))) return true;

                return !Hold();
            }
            catch
            {
                return true;
            }
        }

        private static bool IsTeammateAtSceneAlone(Vector2 scene)
        {
            if (!PlayerAccess.TryGetPlayer(out Actor self)) return false;

            if ((self.GetFeetPosition() - scene).sqrMagnitude <= SceneReach * SceneReach) return false;

            return RemotePlayers.IsAnyWithin(scene, SceneReach);
        }

        private static bool Hold()
        {
            Held++;
            GateFollow.Arm();
            return true;
        }

        // The first creature's authored place, since the observer may sit far from the arena.
        private static Vector2 WavePosition(GlobalObserverBhvr_WavesSpawning observer)
        {
            for (int wave = 0; wave < observer._waves.Count; wave++)
            {
                Il2CppSystem.Collections.Generic.List<Il2CppDrova.Utilities.LazyLoading.LazyActor> actors =
                    observer._waves[wave]?.Actors;
                if (actors == null) continue;

                for (int index = 0; index < actors.Count; index++)
                {
                    if (actors[index] != null) return actors[index].GetFeetPosition();
                }
            }

            return observer.transform.position;
        }

        private static Vector2 ScenePosition(GlobalObserverBhvr_StartDialogue observer)
        {
            EntityInfo? owner = observer._dialogueOwner;

            if (owner != null)
            {
                Actor? actor = owner.GetActorFromInfo();
                if (actor != null) return actor.GetFeetPosition();

                Il2CppDrova.Utilities.LazyLoading.LazyActor? lazy = owner.GetLazyActorFromInfo();
                if (lazy != null) return lazy.GetFeetPosition();
            }

            return observer.transform.position;
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings) => Reset();
    }
}
