using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Morphing;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.Players
{
    /// <summary>
    /// Keeps a body in the world for every other player in the session, and moves it. Reconciled
    /// against the roster every frame, so a missed join or leave cannot drift it.
    /// </summary>
    internal sealed class RemotePlayers : ICoopSystem
    {
        private static readonly Dictionary<PlayerId, RemotePlayer> BodyById = new();
        private static readonly List<PlayerId> Departed = new();

        // A morphed teammate's human body waits hidden here while the creature stands in for it.
        private static readonly Dictionary<PlayerId, RemotePlayer> HumanById = new();
        private static readonly Dictionary<PlayerId, string> CreatureById = new();

        // Rebuilt at most once a frame, because the asking patches sit on the game's hot paths.
        private static readonly List<Vector2> PositionBuffer = new();
        private static int _positionsFrame = -1;

        /// <summary>
        /// How many bodies are currently in the world.
        /// </summary>
        internal static int Count => BodyById.Count;

        /// <summary>
        /// The bodies in the world, by the player they belong to.
        /// </summary>
        internal static Dictionary<PlayerId, RemotePlayer> Bodies => BodyById;

        /// <summary>
        /// Where every remote player stands, as feet position like the game's own distance tests.
        /// </summary>
        internal static IReadOnlyList<Vector2> Positions
        {
            get
            {
                if (_positionsFrame == Time.frameCount) return PositionBuffer;

                _positionsFrame = Time.frameCount;
                PositionBuffer.Clear();

                foreach (KeyValuePair<PlayerId, RemotePlayer> body in BodyById)
                {
                    Actor actor = body.Value.Body;
                    if (actor != null)
                    {
                        PositionBuffer.Add(actor.GetFeetPosition());
                    }
                }

                return PositionBuffer;
            }
        }

        /// <summary>
        /// Whether any remote player is standing within <paramref name="range"/> of a point.
        /// </summary>
        internal static bool IsAnyWithin(Vector2 point, float range)
        {
            if (BodyById.Count == 0) return false;

            IReadOnlyList<Vector2> positions = Positions;
            float squared = range * range;

            for (int index = 0; index < positions.Count; index++)
            {
                if ((positions[index] - point).sqrMagnitude <= squared) return true;
            }

            return false;
        }

        /// <summary>
        /// Whether an entity is one of the bodies this mod puppets for a remote player. Cheap, since every ability hit asks.
        /// </summary>
        internal static bool IsPuppet(Il2CppObjectBase? entity) => entity != null && TryFindOwner(entity.Pointer, out _);

        /// <summary>
        /// Whether an entity is the person playing at this machine. Null is not.
        /// </summary>
        internal static bool IsLocalPlayer(Il2CppObjectBase? entity)
        {
            if (entity == null) return false;

            return PlayerAccess.TryGetPlayer(out Actor player) && player.Pointer == entity.Pointer;
        }

        /// <summary>
        /// Whether this body is the local player or a remote player's puppet. Takes any object, so a caller can ask before casting.
        /// </summary>
        internal static bool IsPlayersBody(Il2CppObjectBase? actor)
        {
            return IsPuppet(actor) || IsLocalPlayer(actor);
        }

        /// <summary>
        /// Which player, if any, a health component belongs to the body of. Cheap, since the damage funnel asks for every actor.
        /// </summary>
        internal static bool TryFindOwner(Health? health, out PlayerId id)
        {
            id = PlayerId.None;

            if (health == null) return false;

            return TryFindOwner(health.OwnerEntity, out id);
        }

        /// <summary>
        /// Which player an entity is the body of, if any.
        /// </summary>
        internal static bool TryFindOwner(Entity? entity, out PlayerId id)
        {
            id = PlayerId.None;

            if (entity == null) return false;

            return TryFindOwner(entity.Pointer, out id);
        }

        private static bool TryFindOwner(IntPtr pointer, out PlayerId id)
        {
            id = PlayerId.None;

            if (BodyById.Count == 0) return false;

            foreach (KeyValuePair<PlayerId, RemotePlayer> body in BodyById)
            {
                if (body.Value.Body == null) continue;
                if (body.Value.Body.Pointer != pointer) continue;

                id = body.Key;
                return true;
            }

            return false;
        }

        /// <summary>Spawns, despawns and drives remote bodies.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (!CoopSession.IsLive)
            {
                Clear();
                return;
            }

            // PlayerAccess reports a player only once the world is up, the readiness signal for bodies.
            if (!PlayerAccess.TryGetPlayer(out Actor localPlayer)) return;

            SyncRoster(localPlayer);
            ApplyMorphs();
            ApplyStates(unscaledDeltaTime);
        }

        /// <summary>
        /// Removes every body. Called when a session ends or the world goes away.
        /// </summary>
        private static void Clear()
        {
            if (BodyById.Count == 0) return;

            foreach (RemotePlayer body in BodyById.Values)
            {
                body.ReleaseLocalHostility();
                TeammateMenu.Detach(body.Body);
                PuppetFactory.Destroy(body.Body);
            }

            foreach (RemotePlayer human in HumanById.Values)
            {
                PuppetFactory.Destroy(human.Body);
            }

            BodyById.Clear();
            HumanById.Clear();
            CreatureById.Clear();
            MelonLogger.Msg("[Puppet] all remote bodies removed.");
        }

        private static void SyncRoster(Actor localPlayer)
        {
            DropBodiesThatDidNotSurvive();

            foreach (KeyValuePair<PlayerId, string> player in CoopSession.Players)
            {
                if (player.Key == CoopSession.LocalPlayerId) continue;
                if (BodyById.ContainsKey(player.Key)) continue;

                // Spawned on the local player, not the origin, so it does not slide across the map.
                if (!PuppetFactory.TryCreate($"DrovaCoop.Remote.{player.Key.Value}", localPlayer.transform.position, out Actor? body))
                {
                    continue;
                }

                RemotePlayer created = new(player.Key, body);
                TeammateMenu.Attach(body, player.Key);

                // Adopt the last cast without performing it, or a new body appears mid-cast.
                if (ItemUseWatch.ItemUses.TryGetValue(player.Key, out ItemUseSnapshot pending))
                {
                    created.AdoptItemUse(pending);
                }

                BodyById[player.Key] = created;
                MelonLogger.Msg($"[Puppet] body created for {player.Key} '{player.Value}'.");
            }

            Departed.Clear();
            foreach (PlayerId present in BodyById.Keys)
            {
                if (!CoopSession.Players.ContainsKey(present))
                {
                    Departed.Add(present);
                }
            }

            foreach (PlayerId gone in Departed)
            {
                BodyById[gone].ReleaseLocalHostility();
                TeammateMenu.Detach(BodyById[gone].Body);
                PuppetFactory.Destroy(BodyById[gone].Body);
                BodyById.Remove(gone);
                if (HumanById.Remove(gone, out RemotePlayer? human)) PuppetFactory.Destroy(human.Body);
                CreatureById.Remove(gone);
                MelonLogger.Msg($"[Puppet] body removed for {gone}.");
            }
        }

        /// <summary>
        /// Forgets bodies whose actor no longer exists, so the roster rebuilds them. Loading a save
        /// destroys every puppet without passing the menu, so <see cref="Clear"/> never runs.
        /// </summary>
        private static void DropBodiesThatDidNotSurvive()
        {
            if (BodyById.Count == 0) return;

            Departed.Clear();

            foreach (KeyValuePair<PlayerId, RemotePlayer> body in BodyById)
            {
                if (body.Value.Body == null)
                {
                    Departed.Add(body.Key);
                }
            }

            foreach (PlayerId lost in Departed)
            {
                // Only forgotten, since the actor is already destroyed.
                TeammateMenu.Detach(BodyById[lost].Body);
                BodyById.Remove(lost);
                HumanById.Remove(lost);
                CreatureById.Remove(lost);
                MelonLogger.Msg($"[Puppet] the body for {lost} did not survive the world; rebuilding it.");
            }
        }

        /// <summary>
        /// Swaps a teammate's body for the creature they are morphed into, and back when the morph ends.
        /// </summary>
        private static void ApplyMorphs()
        {
            if (MorphBroadcaster.Creatures.Count == 0 && CreatureById.Count == 0) return;

            foreach (PlayerId id in BodyById.Keys.ToList())
            {
                string wanted = MorphBroadcaster.Creatures.TryGetValue(id, out string? guid) ? guid : string.Empty;
                string current = CreatureById.TryGetValue(id, out string? morphed) ? morphed : string.Empty;
                if (wanted == current) continue;

                if (current.Length > 0) Unmorph(id);
                if (wanted.Length > 0) Morph(id, wanted);
            }
        }

        private static void Morph(PlayerId id, string guid)
        {
            // Retried next frame while the creature loads.
            if (!MorphManager.TryGetCreature(guid, out Actor? creature)) return;
            if (!BodyById.TryGetValue(id, out RemotePlayer? human) || human.Body == null) return;

            if (!PuppetFactory.TryCreate($"DrovaCoop.Remote.{id.Value}.Morph", human.Body.transform.position, out Actor? body, creature))
            {
                return;
            }

            MorphManager.EquipCreatureWeapon(body);
            human.ReleaseLocalHostility();
            TeammateMenu.Detach(human.Body);
            human.Body.SetActiveInGame(false);
            HumanById[id] = human;

            BodyById[id] = new RemotePlayer(id, body);
            TeammateMenu.Attach(body, id);
            CreatureById[id] = guid;
            MelonLogger.Msg($"[Morph] {id} is now {creature.name}.");
        }

        private static void Unmorph(PlayerId id)
        {
            CreatureById.Remove(id);
            if (!BodyById.Remove(id, out RemotePlayer? morphed)) return;

            Actor? creature = morphed.Body;
            morphed.ReleaseLocalHostility();
            TeammateMenu.Detach(creature);

            // Without its human body the roster builds a fresh one next frame.
            if (HumanById.Remove(id, out RemotePlayer? human) && human.Body != null)
            {
                if (creature != null) human.Body.transform.position = creature.transform.position;
                human.Body.SetActiveInGame(true);
                PuppetFactory.Constrain(human.Body);
                TeammateMenu.Attach(human.Body, id);
                BodyById[id] = human;
            }

            PuppetFactory.Destroy(creature);
            MelonLogger.Msg($"[Morph] {id} is human again.");
        }

        /// <summary>
        /// Feeds each body whatever arrived for it and lets the game move it.
        /// </summary>
        private static void ApplyStates(float unscaledDeltaTime)
        {
            foreach (KeyValuePair<PlayerId, RemotePlayer> body in BodyById)
            {
                if (CoopSession.LatestStates.TryGetValue(body.Key, out PlayerStateMessage state))
                {
                    body.Value.Record(state);
                }

                if (LocalPlayerBroadcaster.Vitals.TryGetValue(body.Key, out PlayerVitalsMessage vitals))
                {
                    body.Value.RecordVitals(vitals);
                }

                // A creature keeps its own weapon. The owner's gear, talents and items belong to the human body.
                bool creature = CreatureById.ContainsKey(body.Key);

                // Before the intent, so a swing uses the weapon that arrived in the same frame.
                if (!creature && EquipmentBroadcaster.Equipment.TryGetValue(body.Key, out EquipmentSnapshot equipment))
                {
                    body.Value.RecordEquipment(equipment);
                }

                // Before the intent, like the equipment: a swing is shaped by what its owner has learned.
                if (!creature && TalentBroadcaster.Talents.TryGetValue(body.Key, out (string[] Guids, int Version) talents))
                {
                    body.Value.RecordTalents(talents.Guids, talents.Version);
                }

                // After the equipment, since some abilities pick their effect from the held weapon.
                if (!creature && ItemUseWatch.ItemUses.TryGetValue(body.Key, out ItemUseSnapshot cast))
                {
                    body.Value.RecordItemUse(cast);
                }

                // Every frame, since the movement module expects a direction each tick like the input layer.
                body.Value.Apply(unscaledDeltaTime);
            }
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (!loaded) Clear();
        }
    }
}
