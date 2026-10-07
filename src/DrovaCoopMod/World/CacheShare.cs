using Drova_Modding_API;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Il2CppDrova;
using Il2CppDrova.Saveables;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Shares a breakable cache's contents as the item keys and counts the game saves, applied down its load path.
    /// Also keeps spilled pickups, whose guids are random per clone, out of <see cref="LootShare"/>'s ledger under any rule.
    /// </summary>
    internal static class CacheShare
    {
        /// <summary>The most caches a joining player is told about.</summary>
        private const int MaxSnapshot = 256;

        private static readonly Dictionary<string, Saveable_LootTablePickups> CacheByKey = new(StringComparer.Ordinal);

        private static readonly Dictionary<int, string> KeyByCache = new();

        /// <summary>What the session agrees each cache holds.</summary>
        private static readonly Dictionary<string, CacheSpillMessage> Ledger = new(StringComparer.Ordinal);

        /// <summary>Pickups this machine watched a cache spill, by instance. Nothing on the object says so.</summary>
        private static readonly HashSet<int> SpilledIds = new();

        /// <summary>How many of the three patches applied.</summary>
        internal static int HooksInstalled { get; private set; }

        /// <summary>How many caches this machine told the session about.</summary>
        internal static int Sent { get; private set; }

        /// <summary>How many caches this machine set to a teammate's contents.</summary>
        internal static int Applied { get; private set; }

        /// <summary>Host only: how many offers lost to one that already stood.</summary>
        internal static int Refused { get; private set; }

        /// <summary>How many caches are loaded here under a name a teammate could use.</summary>
        internal static int Addressable => CacheByKey.Count;

        /// <summary>How many spilled pickups are being kept out of the loot ledger.</summary>
        internal static int Spilled => SpilledIds.Count;

        private static bool Shared => CoopSession.LootMode == CoopProtocol.LootModeShared;

        /// <summary>Whether a pickup was spilled by a cache rather than authored into the scene.</summary>
        internal static bool WasSpilled(Component pickup)
        {
            if (pickup == null || SpilledIds.Count == 0) return false;

            return SpilledIds.Contains(pickup.gameObject.GetInstanceID());
        }

        /// <summary>Applies the patches. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            int applied = 0;

            // Where a cache is bound to its save root and first has a name.
            if (Hooking.TryPostfix(harmony, typeof(Saveable_LootTablePickups), nameof(Saveable_LootTablePickups.OnInitRoot),
                typeof(CacheShare), nameof(InitRootPostfix))) applied++;

            // A cache spilling, on a break and on a load alike.
            if (Hooking.TryPostfix(harmony, typeof(Saveable_LootTablePickups), "OnSpawnObjectEventListener",
                typeof(CacheShare), nameof(SpawnPostfix))) applied++;

            if (Hooking.TryPostfix(harmony, typeof(Saveable_LootTablePickups), "ObjectLootedEventListener",
                typeof(CacheShare), nameof(LootedPostfix))) applied++;

            HooksInstalled = applied;

            if (applied != 3)
            {
                MelonLogger.Warning($"[Cache] {applied} of 3 hooks installed; spilled pickups may reach the loot " +
                    "ledger under names no teammate can match.");
            }
        }

        /// <summary>Forgets the session's agreement. Called when a session ends.</summary>
        internal static void Reset()
        {
            Ledger.Clear();
            Sent = 0;
            Applied = 0;
            Refused = 0;

            // Spill marks and the cache table describe the loaded world, so they survive.
        }

        /// <summary>Takes a cache's contents from a teammate.</summary>
        internal static void Park(in CacheSpillMessage message)
        {
            if (!message.IsPlausible()) return;

            // An offer stands only if nothing stands already, or it is the host's over a client's.
            // A remainder always stands.
            if (message.Kind == CacheSpillMessage.KindOffer && Ledger.TryGetValue(message.RootKey, out CacheSpillMessage own)
                && !(message.Player == PlayerId.Host.Value && own.Player != PlayerId.Host.Value)) return;

            Ledger[message.RootKey] = message;
            Apply(message);
        }

        /// <summary>Host only: whether a client's offer is the one that stands.</summary>
        internal static bool TryAcceptOffer(in CacheSpillMessage message, out CacheSpillMessage? standing)
        {
            standing = null;

            if (!Ledger.TryGetValue(message.RootKey, out CacheSpillMessage known))
            {
                Ledger[message.RootKey] = message;
                Apply(message);
                return true;
            }

            Refused++;
            standing = known;
            return false;
        }

        /// <summary>Host only: tells a joining player what the session agrees every cache holds.</summary>
        internal static void SendTo(Drova_Modding_API.Systems.Networking.INetPeer peer)
        {
            int sent = 0;

            foreach (KeyValuePair<string, CacheSpillMessage> entry in Ledger)
            {
                CoopSession.SendCacheSpill(peer, entry.Value);

                if (++sent >= MaxSnapshot) break;
            }
        }

        private static void InitRootPostfix(Saveable_LootTablePickups __instance)
        {
            try
            {
                if (__instance == null) return;

                string? key = WorldObjects.KeyOf(__instance);
                if (key == null) return;

                CacheByKey[key] = __instance;
                KeyByCache[__instance.GetInstanceID()] = key;

                if (Ledger.TryGetValue(key, out CacheSpillMessage entry)) Apply(entry);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Cache] registering a cache failed: " + e);
            }
        }

        private static void SpawnPostfix(Saveable_LootTablePickups __instance, SpawnFromLootTable.SpawnObjectEventArgs arg0)
        {
            try
            {
                if (__instance == null) return;

                // Under any loot rule.
                MarkSpilled(__instance);

                if (arg0.IsLoading) return;
                if (!CoopSession.IsLive || !Shared) return;
                if (RemoteApply<LootScope>.InProgress) return;

                if (!KeyByCache.TryGetValue(__instance.GetInstanceID(), out string key)) return;

                Offer(key, arg0.Keys, arg0.Amount, CacheSpillMessage.KindOffer);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Cache] reporting a cache's contents failed: " + e);
            }
        }

        private static void LootedPostfix(Saveable_LootTablePickups __instance)
        {
            try
            {
                if (__instance == null) return;
                if (!CoopSession.IsLive || !Shared) return;
                if (RemoteApply<LootScope>.InProgress) return;

                if (!KeyByCache.TryGetValue(__instance.GetInstanceID(), out string key)) return;

                // Read after the game decremented its count, so this is the remainder.
                SaveData_LootTable? data = __instance._saveData;
                if (data == null) return;

                Offer(key, data.Keys, data.Amount, CacheSpillMessage.KindRemainder);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Cache] reporting a cache's remainder failed: " + e);
            }
        }

        private static void Offer(string key, Il2CppStringArray? keys, Il2CppStructArray<int>? amounts, byte kind)
        {
            CacheSpillMessage message = new()
            {
                RootKey = key,
                Kind = kind,
                Player = CoopSession.LocalPlayerId.Value,
                Keys = keys?.Select(entry => entry ?? string.Empty).ToArray() ?? [],
                Amounts = amounts?.ToArray() ?? [],
            };

            if (!message.IsPlausible()) return;

            if (kind == CacheSpillMessage.KindOffer && Ledger.ContainsKey(key)) return;

            Ledger[key] = message;
            Sent++;

            CoopSession.ReportCacheSpill(message);
        }

        /// <summary>Sets this world's copy of a cache to what the session agreed, down the game's own load path.</summary>
        private static void Apply(in CacheSpillMessage message)
        {
            if (!CacheByKey.TryGetValue(message.RootKey, out Saveable_LootTablePickups? cache) || cache == null) return;

            try
            {
                SpawnFromLootTable spawner = cache._lootTableSpawn;
                if (spawner == null) return;

                using (RemoteApply<LootScope>.Enter())
                {
                    Clear(spawner);

                    Il2CppStringArray keys = new(message.Keys);
                    Il2CppStructArray<int> amounts = new(message.Amounts);

                    // The call the game makes when it restores a cache from a savegame.
                    spawner.SpawnFromIndexList(keys, amounts);

                    SaveData_LootTable? data = cache._saveData;
                    if (data != null)
                    {
                        data.Keys = keys;
                        data.Amount = amounts;
                    }
                }

                MarkSpilled(cache);
                Applied++;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Cache] setting a cache to a teammate's contents failed: " + e);
            }
        }

        /// <summary>Removes what is lying around a cache, so the agreed set replaces it.</summary>
        private static void Clear(SpawnFromLootTable spawner)
        {
            Il2CppSystem.Collections.Generic.List<GameObject> objects = spawner._objects;
            if (objects == null) return;

            for (int index = 0; index < objects.Count; index++)
            {
                GameObject spawned = objects[index];
                if (spawned == null) continue;

                SpilledIds.Remove(spawned.GetInstanceID());
                UnityEngine.Object.Destroy(spawned);
            }

            objects.Clear();
        }

        /// <summary>Remembers every pickup a cache has lying around, so the loot ledger leaves them be.</summary>
        private static void MarkSpilled(Saveable_LootTablePickups cache)
        {
            SpawnFromLootTable spawner = cache._lootTableSpawn;
            if (spawner == null) return;

            Il2CppSystem.Collections.Generic.List<GameObject> objects = spawner._objects;
            if (objects == null) return;

            for (int index = 0; index < objects.Count; index++)
            {
                GameObject spawned = objects[index];
                if (spawned == null) continue;

                SpilledIds.Add(spawned.GetInstanceID());
            }
        }
    }
}
