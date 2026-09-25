using Drova_Modding_API;
using Drova_Modding_API.Access;
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

        private static bool UpdatePrefix(GlobalObserverBhvr_StartDialogue __instance)
        {
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
