using Drova_Modding_API;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.Enemies
{
    /// <summary>
    /// Keeps the scenes around an NPC from splitting across the session. Each machine starts its own dialogues, so
    /// without this one player sits in an NPC's scene while a teammate fights the same NPC out of another talk.
    /// An NPC in a teammate's scripted fight takes no talk, and a walk-up talk waits for a teammate's talk or fight
    /// with that NPC or beside it. Two players may still each choose to talk to the same NPC, as they always could
    /// (<see cref="World.StoryClaims"/> hides the scenes of that talk from the second one).
    /// The host knows the clients' engagements (<see cref="EnemyEngagements"/>); clients are told the host's and each
    /// other's, and keep them here.
    /// </summary>
    internal sealed class BusyNpcs : ICoopSystem
    {
        /// <summary>Three missed keepalives of <see cref="EngageWatch"/>'s five-second repeat.</summary>
        private const float ExpirySeconds = 15f;

        /// <summary>How near a teammate's running scene an NPC's walk-up talk waits, since the scene may need that NPC next.</summary>
        internal const float SceneRange = 150f;

        /// <summary>Client: when each NPC was last said to be in a teammate's talk or scripted fight, and which of the two.</summary>
        private static readonly Dictionary<EnemyNetId, (float At, byte Mode)> HeardAt = new();

        /// <summary>Talks not started here because a teammate has the NPC. For tests.</summary>
        internal static int Refused { get; private set; }

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            // The one method every talk with an NPC starts through: the player's interaction, a walk-up talk, a scene.
            Hooking.TryPrefix(harmony, typeof(Il2Cpp.DialogueModule), nameof(Il2Cpp.DialogueModule.StartDialogue),
                typeof(BusyNpcs), nameof(StartDialoguePrefix));
        }

        /// <summary>Client: takes what the host said about its own or another client's engagement.</summary>
        internal static void Note(in EnemyEngageMessage message)
        {
            if (message.Mode == EnemyEngageMessage.ModeNone) HeardAt.Remove(message.NetId);
            else HeardAt[message.NetId] = (Time.unscaledTime, message.Mode);
        }

        /// <summary>Whether a teammate is talking to this NPC or fighting it in a scripted brawl.</summary>
        internal static bool IsBusyElsewhere(Actor? npc)
        {
            if (npc == null || !CoopSession.SharesWorld || !EnemyRegistry.TryGetSessionId(npc, out EnemyNetId id)) return false;

            return CoopSession.HostsWorld ? EnemyEngagements.IsHeld(id) : IsMarked(id);
        }

        /// <summary>Whether a teammate fights this NPC in a scripted brawl.</summary>
        internal static bool IsFoughtElsewhere(Actor? npc)
        {
            if (npc == null || !CoopSession.SharesWorld || !EnemyRegistry.TryGetSessionId(npc, out EnemyNetId id)) return false;

            return CoopSession.HostsWorld
                ? EnemyEngagements.TryGetLease(id, out _)
                : IsMarked(id) && HeardAt[id].Mode == EnemyEngageMessage.ModeBrawl;
        }

        /// <summary>Whether a teammate's talk or scripted fight runs within <see cref="SceneRange"/> of a spot.</summary>
        internal static bool IsSceneNear(Vector2 at)
        {
            if (!CoopSession.SharesWorld) return false;
            if (CoopSession.HostsWorld) return EnemyEngagements.IsAnyNear(at, SceneRange);

            foreach (EnemyNetId id in HeardAt.Keys)
            {
                if (!IsMarked(id) || !EnemyRegistry.TryGetBody(id, out Actor? body)) continue;
                if ((body.GetFeetPosition() - at).sqrMagnitude <= SceneRange * SceneRange) return true;
            }

            return false;
        }

        private static bool IsMarked(EnemyNetId id)
        {
            return HeardAt.TryGetValue(id, out (float At, byte Mode) heard) && Time.unscaledTime - heard.At < ExpirySeconds;
        }

        /// <summary>Refuses a talk this player would start with an NPC, or with a participant, that a teammate is fighting.</summary>
        internal static bool StartDialoguePrefix(Il2Cpp.DialogueModule __instance, Actor? instigator,
            Il2CppSystem.Collections.Generic.List<EntityInfo>? npcParticipants, ref bool __result)
        {
            try
            {
                // A world dialogue has no instigator and runs on every machine by design.
                if (!CoopSession.SharesWorld || instigator == null || !RemotePlayers.IsLocalPlayer(instigator)) return true;

                bool busy = IsFoughtElsewhere(__instance._actor);

                if (!busy && npcParticipants != null && EntityGameHandler.TryGet(out EntityGameHandler entities) && entities != null)
                {
                    foreach (EntityInfo participant in npcParticipants)
                    {
                        if (participant == null) continue;

                        if (IsFoughtElsewhere(entities.GetFirstEntityByInfo(participant)?.TryCast<Actor>()))
                        {
                            busy = true;
                            break;
                        }
                    }
                }

                if (!busy) return true;

                Refused++;
                if (Refused == 1)
                {
                    MelonLogger.Msg("[Engage] a talk was not started here: a teammate is fighting that NPC in a scripted brawl.");
                }

                __result = false;
                return false;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Engage] deciding whether an NPC is free to talk failed: " + e);
                return true;
            }
        }

        void ICoopSystem.EndSession()
        {
            HeardAt.Clear();
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (loaded) return;

            HeardAt.Clear();
            Refused = 0;
        }
    }
}
