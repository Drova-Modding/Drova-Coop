using Drova_Modding_API;
using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.GlobalVarSystem;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Runs a world-started story scene - a forced dialogue, a wave fight - only on a machine whose
    /// player is at it.
    ///
    /// <c>GlobalObserverBhvr_StartDialogue</c> polls its condition from <c>Update</c>. The flag that
    /// opens it crosses from whoever triggered it, so without this every machine starts the dialogue
    /// for its own player wherever they stand: the flesh boss cutscenes, the Red Tower ending, Eugen's
    /// sleep, the vases, the Brutus room. A far player is dragged into a scene with its NPCs ported to
    /// them - on the host, for real - and its authored walks wait on a player who is not there.
    ///
    /// So while a teammate is at the scene and this player is not, the observer is held and the follow
    /// watch is armed. The party goes together: this player is brought to the teammate, and the moment
    /// they are within reach the held observer runs here too. With nobody else near, it is vanilla.
    ///
    /// Wave fights (the Atlanter weapon dungeon, the Underground Challenge, the Red Tower draugrs, the
    /// flesh dungeon) are the same shape. Their creatures are authored lazy actors, so they are ordinary
    /// shared creatures once active - but each machine activates them from its own wave observer, and
    /// the far one used to be refused outright, so a teammate arriving saw an empty arena until a
    /// reload. The observer's update is paused instead: it keeps its place and resumes on arrival.
    /// </summary>
    internal static class StorySceneLease
    {
        /// <summary>
        /// How close to the dialogue's owner counts as being at the scene. The follow watch's own
        /// distance, so a player brought along always lands inside it.
        /// </summary>
        private const float SceneReach = 3000f;

        /// <summary>How many frames a dialogue or wave observer was held for a teammate's scene. For <c>coop_status</c>.</summary>
        internal static int Held { get; private set; }

        /// <summary>Whether the patch applied.</summary>
        internal static bool Hooked { get; private set; }

        /// <summary>Forgets the count. Called when a session ends.</summary>
        internal static void Reset()
        {
            Held = 0;
        }

        /// <summary>Applies the patch. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            // Update is a Unity message, called by the engine and never inlined. The managed decompile
            // of this class is older than the shipping one, so nothing below reaches past its fields.
            Hooked = Hooking.TryPrefix(harmony, typeof(GlobalObserverBhvr_StartDialogue), "Update",
                typeof(StorySceneLease), nameof(UpdatePrefix));

            // A cognition event listener, reached through a delegate, so not inlined (TriggerDialogue below it may be).
            Hooking.TryPrefix(harmony, typeof(Il2CppDrova.ActorIntentBhvr), "UpdateCognitionListener",
                typeof(StorySceneLease), nameof(IntentPrefix));

            // Reached through IUpdatable, so not inlined.
            Hooked &= Hooking.TryPrefix(harmony, typeof(GlobalObserverBhvr_WavesSpawning), "ManagedUpdate",
                typeof(StorySceneLease), nameof(WavesPrefix));

            if (!Hooked)
            {
                MelonLogger.Warning("[Lease] the story-dialogue or wave observer could not be patched; a " +
                    "teammate's world dialogue may start for this player wherever they are, or a wave fight " +
                    "may run where only one of you is.");
            }
        }

        /// <summary>The game's own notice radius for a walk-up talk (ActorIntentBhvr.SeeRadiusClose).</summary>
        private const float IntentDeferRange = 70f;

        /// <summary>How many walk-up talks a client left to the host. For the log.</summary>
        internal static int IntentsDeferred { get; private set; }

        /// <summary>How many walk-up talks the host left to a teammate already in them. For the log.</summary>
        internal static int HostIntentsDeferred { get; private set; }

        /// <summary>
        /// A client leaves an NPC's walk-up talk (an intent) to the host when the host's player stands by that NPC
        /// too. Both worlds notice a player within 70 units, so with both players there both started the same talk:
        /// the client's copy then walked and ported NPCs the host's copy had already moved (each move rescued after
        /// 30 s) and its story ports pulled the players apart (Molvina's Red Tower talks). The host's world owns the
        /// NPC and the talk's flags cross, so the client simply does not start it; alone by the NPC, it still does.
        /// </summary>
        private static bool IntentPrefix(Il2CppDrova.ActorIntentBhvr __instance)
        {
            try
            {
                // The host's half: a teammate already in this NPC's talk holds it still here (EnemyEngagements). Starting
                // the host's own copy as well ran the talk twice - the host's copy hung on walks the client's had already
                // made, for minutes, while the client fought the next floor alone; a Red Tower wave went uncounted after.
                if (CoopSession.State == CoopSessionState.Hosting)
                {
                    Il2CppDrova.Actor? held = __instance._actor;
                    if (held == null) return true;

                    // Noticed only a teammate's body: the host's player is not within the talk's own notice radius, so it
                    // is the teammate's talk, started in their world. Started here, it pulled the host into Eugen's talk
                    // from across the Red Tower's top floor while the client walking up to him found him busy.
                    if (PlayerAccess.TryGetPlayer(out Il2CppDrova.Actor me)
                        && ((Vector2)(me.GetFeetPosition() - held.GetFeetPosition())).sqrMagnitude > IntentDeferRange * IntentDeferRange
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
                if (!RemotePlayers.Bodies.TryGetValue(PlayerId.Host, out RemotePlayer? host) || host?.Body == null) return true;

                Il2CppDrova.Actor npc = __instance._actor;
                if (npc == null) return true;

                // Only to a host inside the game's own notice radius and nearer the NPC than this player. A wider
                // window left the talk to a host standing just outside it, whose world never started it either:
                // the Remnants' Jero waited for his crystal hand-over with the client at his elbow.
                float hostSq = ((Vector2)(host.Body.GetFeetPosition() - npc.GetFeetPosition())).sqrMagnitude;
                if (hostSq > IntentDeferRange * IntentDeferRange) return true;
                if (PlayerAccess.TryGetPlayer(out Il2CppDrova.Actor self)
                    && ((Vector2)(self.GetFeetPosition() - npc.GetFeetPosition())).sqrMagnitude < hostSq) return true;

                if (IntentsDeferred++ == 0)
                {
                    MelonLogger.Msg("[Lease] an NPC's walk-up talk was left to the host, whose player stands by it too.");
                }

                return false;
            }
            catch
            {
                return true;
            }
        }

        private static bool UpdatePrefix(GlobalObserverBhvr_StartDialogue __instance)
        {
            try
            {
                // Not before its speaker has a body. The start looks the owner up by its EntityInfo and calls into it
                // unchecked; a flag that turns true while that lookup still answers null (Brutus loaded as his own
                // actor, EntityInfo_Brutus_Boss not yet resolving - measured in the probe) throws inside
                // PlayerLoadedCallback after the observer has marked itself started, and the scene is gone for good.
                // Solo it takes a quick arrival; in a session the flag often crosses first. Held only while the owner
                // can still come (it has a lazy actor that is not dead - a dead one never builds a body, and the
                // Remnants massacre kills the owner of the floor-5 end talk) and the condition is actually met.
                if (!__instance._startState && __instance._dialogueOwner is { } owner)
                {
                    Il2CppDrova.Actor? body = owner.GetActorFromInfo();
                    bool building = body == null ? owner.GetLazyActorFromInfo() is { } lazy && !lazy.IsDead() : !body.IsInitialized;
                    if (building && __instance._condition != null && __instance._condition.CheckCondition()) return false;
                }
            }
            catch
            {
            }

            if (RemotePlayers.Count == 0) return true;

            try
            {
                // Only a dialogue that is about to start. Every unfired observer in the world polls
                // here each frame, and holding - and arming the follow - for one whose condition is
                // not met would drag this player to a teammate who merely stands near its NPC.
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

        private static bool WavesPrefix(GlobalObserverBhvr_WavesSpawning __instance)
        {
            if (RemotePlayers.Count == 0) return true;

            try
            {
                // Past the last wave the observer stays registered until its condition drops; nothing
                // is left to hold.
                if (__instance._currentWaveindex >= __instance._waves.Count) return true;
                if (!IsTeammateAtSceneAlone(WavePosition(__instance))) return true;

                return !Hold();
            }
            catch
            {
                return true;
            }
        }

        /// <summary>Whether a teammate is at the scene and this player is not.</summary>
        private static bool IsTeammateAtSceneAlone(Vector2 scene)
        {
            if (!PlayerAccess.TryGetPlayer(out Actor self)) return false;

            if ((self.GetFeetPosition() - scene).sqrMagnitude <= SceneReach * SceneReach) return false;

            return RemotePlayers.IsAnyWithin(scene, SceneReach);
        }

        /// <summary>
        /// Holds, and arms the follow watch so this player is brought to the scene rather than left
        /// out of it.
        /// </summary>
        private static bool Hold()
        {
            Held++;
            GateFollow.Arm();
            return true;
        }

        /// <summary>
        /// Where a wave fight happens: its first creature's authored place. The observer itself may sit
        /// in a scene of global observers far from the arena.
        /// </summary>
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

        /// <summary>
        /// Where the dialogue happens: its owner if loaded or lazily placed, else the observer itself.
        /// </summary>
        private static Vector2 ScenePosition(GlobalObserverBhvr_StartDialogue observer)
        {
            EntityInfo owner = observer._dialogueOwner;

            if (owner != null)
            {
                Actor actor = owner.GetActorFromInfo();
                if (actor != null) return actor.GetFeetPosition();

                Il2CppDrova.Utilities.LazyLoading.LazyActor lazy = owner.GetLazyActorFromInfo();
                if (lazy != null) return lazy.GetFeetPosition();
            }

            return observer.transform.position;
        }
    }
}
