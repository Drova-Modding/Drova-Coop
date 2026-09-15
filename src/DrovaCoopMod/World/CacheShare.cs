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
    /// Shares what a breakable cache spills, and keeps its pickups out of the shared-loot ledger.
    ///
    /// A cache scatters loose pickups instead of opening a window, and those pickups cannot be named.
    /// Their prefab ships with no authored guid, so each clone mints a fresh random one when it wakes -
    /// which is thirty-six characters of hex and therefore indistinguishable, to anything that only
    /// checks the shape of a key, from a real scene object's. That is the second job here and it is a
    /// repair: without it every spilled berry registers with <see cref="LootShare"/> under a name no
    /// other machine can ever hold, filling the ledger with keys that match nothing, and since 0.1.36
    /// writing them into the savegame as well.
    ///
    /// The first job is the feature. Nothing names a pickup; the cache is named, because it is a scene
    /// object with a real save root, and what crosses is the pair of arrays the game itself reduces a
    /// cache to when it saves one - item keys and counts. Pickups of one key are interchangeable, so a
    /// remainder says everything anybody needs, and the far machine applies it down the game's own load
    /// path: clear what is lying there and spawn the agreed set. Positions are not replicated because
    /// the game does not save them either - a reload scatters a cache afresh, so scatter is cosmetic by
    /// the game's own reckoning.
    ///
    /// Under the shared-loot rule only, like the rest of it. The repair runs regardless, because a
    /// ledger full of unmatched keys is wrong whatever the rule says.
    /// </summary>
    internal static class CacheShare
    {
        /// <summary>The most caches a joining player is told about.</summary>
        private const int MaxSnapshot = 256;

        /// <summary>Caches loaded here, by the key both worlds agree on.</summary>
        private static readonly Dictionary<string, Saveable_LootTablePickups> _cacheByKey = new(StringComparer.Ordinal);

        /// <summary>The key each cache was filed under, so a report names what a teammate knows.</summary>
        private static readonly Dictionary<int, string> _keyByCache = new();

        /// <summary>What the session agrees each cache holds.</summary>
        private static readonly Dictionary<string, CacheSpillMessage> _ledger = new(StringComparer.Ordinal);

        /// <summary>
        /// Pickups this machine watched a cache spill, by instance.
        ///
        /// The whole reason <see cref="LootShare"/> can be told to leave them alone. A spilled pickup is
        /// otherwise indistinguishable from an authored one by the time it starts.
        /// </summary>
        private static readonly HashSet<int> _spilled = new();

        private static int _hooksInstalled;

        private static int _sent;

        private static int _applied;

        private static int _refused;

        /// <summary>How many of the three patches applied.</summary>
        internal static int HooksInstalled => _hooksInstalled;

        /// <summary>How many caches this machine told the session about.</summary>
        internal static int Sent => _sent;

        /// <summary>How many caches this machine set to a teammate's contents.</summary>
        internal static int Applied => _applied;

        /// <summary>Host only: how many offers lost to one that already stood.</summary>
        internal static int Refused => _refused;

        /// <summary>How many caches are loaded here under a name a teammate could use.</summary>
        internal static int Addressable => _cacheByKey.Count;

        /// <summary>How many spilled pickups are being kept out of the loot ledger.</summary>
        internal static int Spilled => _spilled.Count;

        private static bool InSession => CoopSession.State is CoopSessionState.Hosting or CoopSessionState.Joined;

        private static bool Shared => CoopSession.LootMode == CoopProtocol.LootModeShared;

        /// <summary>
        /// Whether a pickup was spilled by a cache rather than authored into the scene.
        ///
        /// Asked by <see cref="LootShare"/> before it gives a pickup a name. Answered from what this
        /// machine watched happen, because nothing on the object itself still says so: the guid it
        /// carries by then was minted a moment earlier and looks exactly like an authored one.
        /// </summary>
        internal static bool WasSpilled(Component pickup)
        {
            if (pickup == null || _spilled.Count == 0) return false;

            return _spilled.Contains(pickup.gameObject.GetInstanceID());
        }

        /// <summary>Applies the patches. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            int applied = 0;

            // The sweep that binds a cache to its save root, which is where it first has a name.
            if (Hooking.TryPostfix(harmony, typeof(Saveable_LootTablePickups), nameof(Saveable_LootTablePickups.OnInitRoot),
                typeof(CacheShare), nameof(InitRootPostfix))) applied++;

            // A cache spilling, on a break and on a load alike. A listener the game hands to an event, so
            // its address is taken and it cannot have been inlined away.
            if (Hooking.TryPostfix(harmony, typeof(Saveable_LootTablePickups), "OnSpawnObjectEventListener",
                typeof(CacheShare), nameof(SpawnPostfix))) applied++;

            // One of its pickups being taken. Also a listener.
            if (Hooking.TryPostfix(harmony, typeof(Saveable_LootTablePickups), "ObjectLootedEventListener",
                typeof(CacheShare), nameof(LootedPostfix))) applied++;

            _hooksInstalled = applied;

            if (applied != 3)
            {
                MelonLogger.Warning($"[Cache] {applied} of 3 hooks installed; spilled pickups may reach the loot " +
                    "ledger under names no teammate can match.");
            }
        }

        /// <summary>Forgets the session's agreement. Called when a session ends.</summary>
        internal static void Reset()
        {
            _ledger.Clear();
            _sent = 0;
            _applied = 0;
            _refused = 0;

            // The spill marks and the cache table deliberately survive: both describe what is loaded in
            // this world right now, which a session beginning or ending does not change.
        }

        /// <summary>Takes a cache's contents from a teammate.</summary>
        internal static void Park(in CacheSpillMessage message)
        {
            if (!message.IsPlausible()) return;

            // An offer only stands if nothing stands already. A remainder always does: somebody took
            // something, and that is not a matter of opinion.
            if (message.Kind == CacheSpillMessage.KindOffer && _ledger.ContainsKey(message.RootKey)) return;

            _ledger[message.RootKey] = message;
            Apply(message);
        }

        /// <summary>
        /// Host only: whether a client's offer is the one that stands.
        /// </summary>
        /// <param name="message">The offer.</param>
        /// <param name="standing">What stands instead, when the offer lost.</param>
        internal static bool TryAcceptOffer(in CacheSpillMessage message, out CacheSpillMessage? standing)
        {
            standing = null;

            if (!_ledger.TryGetValue(message.RootKey, out CacheSpillMessage known))
            {
                _ledger[message.RootKey] = message;
                Apply(message);
                return true;
            }

            _refused++;
            standing = known;
            return false;
        }

        /// <summary>Host only: tells a joining player what the session agrees every cache holds.</summary>
        internal static void SendTo(Drova_Modding_API.Systems.Networking.INetPeer peer)
        {
            int sent = 0;

            foreach (KeyValuePair<string, CacheSpillMessage> entry in _ledger)
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

                string? key = KeyOf(__instance);
                if (key == null) return;

                _cacheByKey[key] = __instance;
                _keyByCache[__instance.GetInstanceID()] = key;

                // A cache the session already agreed about, loaded here after the fact.
                if (_ledger.TryGetValue(key, out CacheSpillMessage entry)) Apply(entry);
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

                // The repair, and it runs whatever the session rule is: these objects must never reach
                // the loot ledger under the random name they carry.
                MarkSpilled(__instance);

                // A load is this world restoring what it already agreed to, not news.
                if (arg0.IsLoading) return;
                if (!InSession || !Shared) return;
                if (RemoteApply<LootScope>.InProgress) return;

                if (!_keyByCache.TryGetValue(__instance.GetInstanceID(), out string key)) return;

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
                if (!InSession || !Shared) return;
                if (RemoteApply<LootScope>.InProgress) return;

                if (!_keyByCache.TryGetValue(__instance.GetInstanceID(), out string key)) return;

                // Read after the game has decremented its own count, so what goes out is the remainder
                // rather than the take. The far copy is set to it, the same way a chest's is.
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
                Keys = Copy(keys),
                Amounts = Copy(amounts),
            };

            if (!message.IsPlausible()) return;

            // An offer this machine has already been told the answer to is not news.
            if (kind == CacheSpillMessage.KindOffer && _ledger.ContainsKey(key)) return;

            _ledger[key] = message;
            _sent++;

            CoopSession.ReportCacheSpill(message);
        }

        /// <summary>
        /// Sets this world's copy of a cache to what the session agreed, down the game's own load path.
        /// </summary>
        private static void Apply(in CacheSpillMessage message)
        {
            if (!_cacheByKey.TryGetValue(message.RootKey, out Saveable_LootTablePickups? cache) || cache == null) return;

            try
            {
                SpawnFromLootTable spawner = cache._lootTableSpawn;
                if (spawner == null) return;

                using (RemoteApply<LootScope>.Enter())
                {
                    Clear(spawner);

                    Il2CppStringArray keys = new(message.Keys.Length);
                    Il2CppStructArray<int> amounts = new(message.Amounts.Length);

                    for (int index = 0; index < message.Keys.Length; index++)
                    {
                        keys[index] = message.Keys[index];
                        amounts[index] = message.Amounts[index];
                    }

                    // The same call the game makes when it restores a cache from a savegame, which is why
                    // nothing here has to know how a pickup is built or where it should land.
                    spawner.SpawnFromIndexList(keys, amounts);

                    SaveData_LootTable? data = cache._saveData;
                    if (data != null)
                    {
                        data.Keys = keys;
                        data.Amount = amounts;
                    }
                }

                MarkSpilled(cache);
                _applied++;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Cache] setting a cache to a teammate's contents failed: " + e);
            }
        }

        /// <summary>
        /// Takes away what is lying around a cache, so the agreed set replaces it rather than joining it.
        /// </summary>
        private static void Clear(SpawnFromLootTable spawner)
        {
            Il2CppSystem.Collections.Generic.List<GameObject> objects = spawner._objects;
            if (objects == null) return;

            for (int index = 0; index < objects.Count; index++)
            {
                GameObject spawned = objects[index];
                if (spawned == null) continue;

                _spilled.Remove(spawned.GetInstanceID());
                UnityEngine.Object.Destroy(spawned);
            }

            objects.Clear();
        }

        /// <summary>
        /// Remembers every pickup a cache currently has lying around, so the loot ledger leaves them be.
        /// </summary>
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

                _spilled.Add(spawned.GetInstanceID());
            }
        }

        private static string[] Copy(Il2CppStringArray? source)
        {
            if (source == null) return [];

            string[] copy = new string[source.Length];
            for (int index = 0; index < source.Length; index++)
            {
                copy[index] = source[index] ?? string.Empty;
            }

            return copy;
        }

        private static int[] Copy(Il2CppStructArray<int>? source)
        {
            if (source == null) return [];

            int[] copy = new int[source.Length];
            for (int index = 0; index < source.Length; index++)
            {
                copy[index] = source[index];
            }

            return copy;
        }

        private static string? KeyOf(Component cache)
        {
            ASaveRoot? root = cache.GetComponent<ASaveRoot>();
            if (root == null) root = cache.GetComponentInParent<ASaveRoot>();
            if (root == null) return null;

            SaveRoot_Dynamic? dynamic = root.TryCast<SaveRoot_Dynamic>();
            if (dynamic != null && dynamic.IsDynamic()) return null;

            string key = root.GetGameObjectInstanceKey();
            return ResourceWorkMessage.IsReplicableKey(key) ? key : null;
        }
    }
}
