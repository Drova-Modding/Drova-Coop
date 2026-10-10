using Drova_Modding_API;
using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Enemies.Patches;
using DrovaCoopMod.Systems.Session;
using Il2Cpp;
using Il2CppDrova;
using MelonLoader;

namespace DrovaCoopMod.Systems.Enemies
{
    /// <summary>
    /// Asks the host about creatures this world believes dead, so ones the host killed get laid out here too.
    /// Ones the host has alive are brought back by <see cref="EnemyRegistry"/>.
    /// </summary>
    internal sealed class HostWorldState : ICoopSystem
    {
        private const int BatchSize = WorldStateRequestMessage.MaxGuids;

        /// <summary>How long a guid waits for a verdict before being asked about again.</summary>
        private const float RetrySeconds = 5f;

        /// <summary>How often the corpses already standing are looked for.</summary>
        private const float SweepSeconds = 5f;

        /// <summary>
        /// How close a corpse must be to be asked about. Farther ones are corrected as they stream in, and an
        /// unbounded sweep floods the rebuild queue.
        /// </summary>
        private const float SweepRange = 600f;

        // Everything asked about and not yet answered, with when it was asked.
        private static readonly Dictionary<string, float> Pending = new(StringComparer.Ordinal);
        private static readonly HashSet<string> Answered = new(StringComparer.Ordinal);
        private static readonly List<string> Batch = new();
        private static readonly List<string> Stale = new();

        private static readonly Queue<EnemyGraveyardMessage> IncomingGraveyard = new();
        private static readonly Queue<EnemyAliveMessage> IncomingAlive = new();
        private static readonly Queue<EnemyReviveMessage> IncomingRevive = new();
        private static readonly List<string> GraveyardChunk = new();
        private static readonly List<string> AliveChunk = new();

        private static float _sinceSweep;
        private static bool _aliveFired;
        private static bool _deadFired;

        /// <summary>How many creatures the host has been asked about, for <c>coop_status</c>.</summary>
        internal static int Noticed { get; private set; }

        /// <summary>Which of the two hooks has actually run, for <c>coop_status</c>.</summary>
        internal static string Fired =>
            (_aliveFired ? "IsActorAlive" : "-") + " / " + (_deadFired ? "LazyActor.IsDead" : "-");

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            // Both: LazyActor.IsDead wraps IsActorAlive and either can be inlined away, see Fired.
            Hooking.TryPostfix(harmony, typeof(SavegameDataExtensions), nameof(SavegameDataExtensions.IsActorAlive),
                typeof(HostWorldStatePatches), nameof(HostWorldStatePatches.IsActorAlivePostfix));

            Hooking.TryPostfix(harmony, typeof(Il2CppDrova.Utilities.LazyLoading.LazyActor), "IsDead",
                typeof(HostWorldStatePatches), nameof(HostWorldStatePatches.IsDeadPostfix));
        }

        /// <summary>
        /// The lazy handle decides whether to build a body at all, so it sees creatures the inner method may not.
        /// </summary>
        internal static void IsDeadPostfix(Il2CppDrova.Utilities.LazyLoading.LazyActor __instance, ref bool __result)
        {
            try
            {
                _deadFired = true;

                if (!__result) return;
                if (!CoopSession.JoinedWorld) return;

                string guid = __instance.GUID;
                if (string.IsNullOrEmpty(guid)) return;

                if (EnemyRegistry.IsKnownDead(guid)) return;

                if (!Answered.Contains(guid) && Pending.TryAdd(guid, 0f))
                {
                    Noticed++;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[World] deciding whether a lazy creature is dead failed: " + e);
            }
        }

        /// <summary>Client: asks the host about anything still unanswered.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (!CoopSession.JoinedWorld)
            {
                Clear();
                return;
            }

            _sinceSweep += unscaledDeltaTime;
            if (_sinceSweep >= SweepSeconds)
            {
                _sinceSweep = 0f;
                SweepStandingCorpses();
            }

            if (Pending.Count == 0) return;

            try
            {
                Batch.Clear();
                Stale.Clear();

                foreach (KeyValuePair<string, float> waiting in Pending)
                {
                    if (waiting.Value > 0f && waiting.Value + RetrySeconds > UnityEngine.Time.unscaledTime) continue;

                    Batch.Add(waiting.Key);
                    Stale.Add(waiting.Key);

                    if (Batch.Count >= BatchSize) break;
                }

                foreach (string guid in Stale)
                {
                    Pending[guid] = UnityEngine.Time.unscaledTime;
                }

                if (Batch.Count > 0)
                {
                    AskAboutWorldState(Batch);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[World] asking the host what it has killed failed: " + e);
            }
        }

        /// <summary>Forgets everything. Called when a session ends or the world goes away.</summary>
        private static void Clear()
        {
            Pending.Clear();
            Answered.Clear();
            Noticed = 0;
            _sinceSweep = 0f;
        }

        /// <summary>
        /// Queues corpses that were already built before the session started, which the hooks never see again.
        /// </summary>
        private static void SweepStandingCorpses()
        {
            try
            {
                if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return;

                Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppDrova.Utilities.LazyLoading.LazyActor>
                    handles = entities._lazyActorGuidDictionary;
                if (handles == null) return;

                if (!PlayerAccess.TryGetPlayer(out Actor? player)) return;

                UnityEngine.Vector2 origin = player.transform.position;
                const float squared = SweepRange * SweepRange;

                foreach (Il2CppSystem.Collections.Generic.KeyValuePair<string,
                    Il2CppDrova.Utilities.LazyLoading.LazyActor> pair in handles)
                {
                    string guid = pair.Key;

                    // Local set lookups first, before anything crosses the interop boundary.
                    if (string.IsNullOrEmpty(guid)) continue;
                    if (EnemyRegistry.IsKnownDead(guid)) continue;

                    // Minted here, so the host has no record, and no record reads as alive.
                    if (ActorSpawnAccess.IsRuntimeCreated(guid)) continue;

                    Il2CppDrova.Utilities.LazyLoading.LazyActor? lazy = pair.Value;
                    if (lazy == null) continue;

                    if ((lazy.Position - origin).sqrMagnitude > squared) continue;

                    if (!lazy.IsDead()) continue;

                    // Called alive but still lying here, so the rebuild was lost. Retry only while off screen,
                    // since a rebuild visibly swaps the corpse for a live creature.
                    if (EnemyRegistry.IsKnownAlive(guid))
                    {
                        Actor? corpse = entities.GetActorFromGuid(guid);

                        if (corpse == null || !corpse.IsVisible)
                        {
                            EnemyRebirth.Request(guid);
                        }

                        continue;
                    }

                    if (!Answered.Contains(guid) && Pending.TryAdd(guid, 0f))
                    {
                        Noticed++;
                    }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[World] looking for corpses the host was never asked about failed: " + e);
            }
        }

        /// <summary>A savegame was loaded: ask about every creature again.</summary>
        private static void ForgetAnswered() => Answered.Clear();

        /// <summary>Records the host's verdict on a creature.</summary>
        internal static void RecordAnswered(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;

            Pending.Remove(guid);
            Answered.Add(guid);
        }

        /// <summary>Queues a creature this save calls dead, without changing the answer.</summary>
        internal static void IsActorAlivePostfix(string actorGuid, ref bool __result)
        {
            try
            {
                _aliveFired = true;

                // No save record also reads as alive.
                if (__result) return;

                if (!CoopSession.JoinedWorld) return;
                if (string.IsNullOrEmpty(actorGuid)) return;

                if (EnemyRegistry.IsKnownDead(actorGuid)) return;

                if (!Answered.Contains(actorGuid) && Pending.TryAdd(actorGuid, 0f))
                {
                    Noticed++;
                }
            }
            catch (Exception e)
            {
                // Swallowed: this runs inside save loading, and a throw would lose a whole chunk of the world.
                MelonLogger.Error("[World] deciding whether a creature is alive failed: " + e);
            }
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<EnemyGraveyardMessage>(MessageIds.EnemyGraveyard,
                (_, m) => CoopSession.QueueFromHost(m, IncomingGraveyard, CoopSession.MaxPendingFromHost, "graveyard chunks"));
            CoopSession.Register<EnemyAliveMessage>(MessageIds.EnemyAlive,
                (_, m) => CoopSession.QueueFromHost(m, IncomingAlive, CoopSession.MaxPendingFromHost, "alive answers"));
            CoopSession.Register<EnemyReviveMessage>(MessageIds.EnemyRevive,
                (_, m) => CoopSession.QueueFromHost(m, IncomingRevive, CoopSession.MaxPendingFromHost, "revive decrees"));
            CoopSession.Register<WorldStateRequestMessage>(MessageIds.WorldStateRequest, OnWorldStateRequest);
        }

        void ICoopSystem.EndSession()
        {
            IncomingGraveyard.Clear();
            IncomingAlive.Clear();
            IncomingRevive.Clear();
        }

        /// <summary>Sends everything already dead to somebody who has just joined, in chunks.</summary>
        internal static void SendGraveyard(INetPeer peer, IReadOnlyCollection<string> dead)
        {
            if (!CoopSession.HostsWorld) return;

            // The last chunk always goes, even empty, since a short chunk is what ends the list.
            foreach (string[] chunk in dead.Chunk(EnemyGraveyardMessage.MaxGuids))
            {
                bool last = chunk.Length < EnemyGraveyardMessage.MaxGuids;

                NetworkAccess.Send(peer, new EnemyGraveyardMessage { Guids = chunk, More = last ? (byte)0 : (byte)1 });

                if (last) return;
            }

            NetworkAccess.Send(peer, new EnemyGraveyardMessage { Guids = [], More = 0 });
        }

        private static void SendGraveyardChunk(INetPeer peer, bool more)
        {
            NetworkAccess.Send(peer, new EnemyGraveyardMessage
            {
                Guids = [.. GraveyardChunk],
                More = more ? (byte)1 : (byte)0,
            });

            GraveyardChunk.Clear();
        }

        /// <summary>Asks the host which of these creatures its world has already killed.</summary>
        private static void AskAboutWorldState(List<string> guids)
        {
            if (!CoopSession.JoinedWorld || guids.Count == 0) return;

            NetworkAccess.SendToAll(new WorldStateRequestMessage { Guids = guids.ToArray() });
        }

        private static void OnWorldStateRequest(INetPeer peer, WorldStateRequestMessage message)
        {
            if (!CoopSession.HostsWorld || message.Guids == null) return;

            GraveyardChunk.Clear();
            AliveChunk.Clear();

            foreach (string guid in message.Guids)
            {
                if (string.IsNullOrEmpty(guid)) continue;

                // Checked before the save, which keeps a revived creature dead until the next autosave.
                if (EnemyRegistry.IsKnownAlive(guid))
                {
                    AliveChunk.Add(guid);

                    if (AliveChunk.Count >= EnemyAliveMessage.MaxGuids)
                    {
                        SendAliveChunk(peer);
                    }

                    continue;
                }

                // The save counts too: a creature the host killed solo long ago was never mentioned on the wire.
                if (RemoteEnemyDeath.Dead.Contains(guid) || !SavegameDataExtensions.IsActorAlive(guid))
                {
                    GraveyardChunk.Add(guid);

                    if (GraveyardChunk.Count >= EnemyGraveyardMessage.MaxGuids)
                    {
                        SendGraveyardChunk(peer, more: true);
                    }

                    continue;
                }

                // Alive is said explicitly, since the client only asks about its own corpses.
                AliveChunk.Add(guid);

                if (AliveChunk.Count >= EnemyAliveMessage.MaxGuids)
                {
                    SendAliveChunk(peer);
                }
            }

            // Always sent: silence looks like a lost packet and the client would ask forever.
            SendGraveyardChunk(peer, more: false);

            if (AliveChunk.Count > 0)
            {
                SendAliveChunk(peer);
            }
        }

        private static void SendAliveChunk(INetPeer peer)
        {
            NetworkAccess.Send(peer, new EnemyAliveMessage
            {
                Guids = [.. AliveChunk],
            });

            AliveChunk.Clear();
        }

        /// <summary>Takes the next graveyard chunk that has arrived.</summary>
        internal static bool TryTakeGraveyard(out EnemyGraveyardMessage message)
            => IncomingGraveyard.TryDequeue(out message);

        /// <summary>Takes the next chunk of creatures the host has said are alive.</summary>
        internal static bool TryTakeAlive(out EnemyAliveMessage message)
            => IncomingAlive.TryDequeue(out message);

        /// <summary>Takes the next chunk of creatures the host has revived by decree.</summary>
        internal static bool TryTakeRevive(out EnemyReviveMessage message)
            => IncomingRevive.TryDequeue(out message);

        /// <summary>
        /// Tells every client these creatures are alive again. The push half of a revive, the answer half is
        /// <see cref="RemoteEnemyDeath.ForgetEnemyDeath"/>.
        /// </summary>
        internal static void AnnounceEnemyRevive(List<string> guids)
        {
            if (!CoopSession.HostsWorld || guids.Count == 0) return;

            foreach (string[] chunk in guids.Chunk(EnemyReviveMessage.MaxGuids))
            {
                NetworkAccess.SendToAll(new EnemyReviveMessage { Guids = chunk });
            }
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            ForgetAnswered();

            if (!loaded) Clear();
        }
    }
}
