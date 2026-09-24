using Drova_Modding_API;
using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Drova_Modding_API.Systems.Networking;
using Il2CppDrova;
using Il2CppDrova.MapSystem;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Unlocks for everybody the maps one player was handed.
    ///
    /// A map is granted by a dialogue task, and a dialogue runs for the player standing in it. The
    /// unlock crosses by the definition's authored guid: live when granted, and as a walk of the
    /// unlocked list when a player joins. Applied through the game's own enable call, which is
    /// change-guarded, so repeats and echoes do nothing.
    /// </summary>
    internal static class MapShare
    {
        /// <summary>How many unlocks this machine told the session about. For <c>coop_status</c>.</summary>
        internal static int Shared { get; private set; }

        /// <summary>How many unlocks arrived and were applied here. For <c>coop_status</c>.</summary>
        internal static int Received { get; private set; }

        /// <summary>Forgets the counts. Called when a session ends.</summary>
        internal static void Reset()
        {
            Shared = 0;
            Received = 0;
        }

        /// <summary>Applies the unlock patch. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPostfix(harmony, typeof(MapData), nameof(MapData.SetMapEnabled),
                typeof(MapShare), nameof(SetMapEnabledPostfix));
        }

        /// <summary>
        /// Sends every unlocked map to one peer. Called by the host when a player joins.
        /// </summary>
        internal static void SendTo(INetPeer peer)
        {
            try
            {
                if (!TryGetCollection(out MapCollection collection)) return;

                Il2CppSystem.Collections.Generic.List<MapData> maps = collection.GetMapDataList();
                if (maps == null) return;

                int sent = 0;

                for (int index = 0; index < maps.Count; index++)
                {
                    MapData map = maps[index];
                    if (map == null || !map.IsEnabled) continue;

                    string guid = map.Definition?.GUID;
                    if (string.IsNullOrEmpty(guid)) continue;

                    sent++;
                    NetworkAccess.Send(peer, new MapUnlockMessage { Guid = guid! });
                }

                if (sent > 0)
                {
                    MelonLogger.Msg($"[Map] a joining player was sent {sent} map unlock(s).");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Map] walking the maps for a joining player failed: " + e);
            }
        }

        /// <summary>
        /// Applies everything that has arrived. Call once per frame from a loaded world.
        /// </summary>
        internal static void Update()
        {
            if (!CoopSession.IsLive)
            {
                Reset();
                return;
            }

            while (CoopSession.TryTakeMapUnlock(out MapUnlockMessage message))
            {
                Apply(message.Guid);
            }
        }

        private static void Apply(string guid)
        {
            try
            {
                if (string.IsNullOrEmpty(guid)) return;
                if (!TryGetCollection(out MapCollection collection)) return;

                MapDefinition definition = collection.FindMapDefinition(guid);
                if (definition == null) return;

                MapData map = collection.GetMapData(definition);
                if (map == null || map.IsEnabled) return;

                using (RemoteApply<JournalScope>.Enter())
                {
                    map.SetMapEnabled(true, bySavingLoading: false);
                }

                Received++;

                MelonLogger.Msg($"[Map] '{definition.name}' was unlocked because a teammate earned it.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Map] applying a shared unlock failed: " + e);
            }
        }

        private static void SetMapEnabledPostfix(MapData __instance, bool enabled, bool bySavingLoading)
        {
            try
            {
                if (!enabled || bySavingLoading) return;
                if (!CoopSession.IsLive) return;
                if (RemoteApply<JournalScope>.InProgress) return;

                string guid = __instance?.Definition?.GUID;
                if (string.IsNullOrEmpty(guid)) return;

                Shared++;
                CoopSession.ReportMapUnlock(guid!);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Map] reporting an unlock failed: " + e);
            }
        }

        private static bool TryGetCollection(out MapCollection collection)
        {
            collection = null!;

            if (!PlayerMetaDataGameHandler.TryGet(out PlayerMetaDataGameHandler handler) || handler == null)
            {
                return false;
            }

            MapCollection found = handler.GetMapCollection();
            if (found == null) return false;

            collection = found;
            return true;
        }
    }
}
