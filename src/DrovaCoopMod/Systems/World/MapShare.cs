using System.Diagnostics.CodeAnalysis;
using Drova_Modding_API;
using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Sync;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.Systems.World.Patches;
using Il2CppDrova;
using Il2CppDrova.MapSystem;
using MelonLoader;

namespace DrovaCoopMod.Systems.World
{
    /// <summary>
    /// Unlocks for everybody the maps one player was handed, live and at join. The game's enable call is change-guarded, so echoes do nothing.
    /// </summary>
    internal sealed class MapShare : ICoopSystem
    {
        private static readonly Queue<MapUnlockMessage> IncomingMapUnlocks = new();

        /// <summary>How many unlocks this machine told the session about. For <c>coop_status</c>.</summary>
        internal static int Shared { get; private set; }

        /// <summary>How many unlocks arrived and were applied here. For <c>coop_status</c>.</summary>
        internal static int Received { get; private set; }

        /// <summary>Forgets the counts. Called when a session ends.</summary>
        private static void Reset()
        {
            Shared = 0;
            Received = 0;
        }

        /// <summary>Applies the unlock patch.</summary>
        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPostfix(harmony, typeof(MapData), nameof(MapData.SetMapEnabled),
                typeof(MapSharePatches), nameof(MapSharePatches.SetMapEnabledPostfix));
        }

        /// <summary>Sends every unlocked map to one peer. Called by the host when a player joins.</summary>
        internal static void SendTo(INetPeer peer)
        {
            try
            {
                if (!TryGetCollection(out MapCollection? collection)) return;

                Il2CppSystem.Collections.Generic.List<MapData> maps = collection.GetMapDataList();
                if (maps == null) return;

                int sent = 0;

                for (int index = 0; index < maps.Count; index++)
                {
                    MapData map = maps[index];
                    if (map == null || !map.IsEnabled || map.Definition == null) continue;

                    string guid = map.Definition.GUID;
                    if (string.IsNullOrEmpty(guid)) continue;

                    sent++;
                    NetworkAccess.Send(peer, new MapUnlockMessage { Guid = guid });
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

        /// <summary>Applies everything that has arrived.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (!CoopSession.SharesWorld)
            {
                Reset();
                return;
            }

            while (TryTakeMapUnlock(out MapUnlockMessage message))
            {
                Apply(message.Guid);
            }
        }

        private static void Apply(string guid)
        {
            try
            {
                if (string.IsNullOrEmpty(guid)) return;
                if (!TryGetCollection(out MapCollection? collection)) return;

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

        internal static void SetMapEnabledPostfix(MapData __instance, bool enabled, bool bySavingLoading)
        {
            try
            {
                if (!enabled || bySavingLoading) return;
                if (!CoopSession.SharesWorld) return;
                if (RemoteApply<JournalScope>.InProgress) return;

                if (__instance.Definition == null) return;

                string guid = __instance.Definition.GUID;
                if (string.IsNullOrEmpty(guid)) return;

                Shared++;
                ReportMapUnlock(guid);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Map] reporting an unlock failed: " + e);
            }
        }

        private static bool TryGetCollection([NotNullWhen(true)] out MapCollection? collection)
        {
            collection = null;

            if (!PlayerMetaDataGameHandler.TryGet(out PlayerMetaDataGameHandler handler) || handler == null)
            {
                return false;
            }

            MapCollection found = handler.GetMapCollection();
            if (found == null) return false;

            collection = found;
            return true;
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<MapUnlockMessage>(MessageIds.MapUnlock,
                (peer, m) => CoopSession.RelayAndQueue(peer, m, IncomingMapUnlocks, CoopSession.MaxPendingFromHost, "map unlocks"));
        }

        void ICoopSystem.EndSession()
        {
            IncomingMapUnlocks.Clear();
        }

        /// <summary>Takes the next shared map unlock that arrived.</summary>
        private static bool TryTakeMapUnlock(out MapUnlockMessage message)
            => IncomingMapUnlocks.TryDequeue(out message);

        /// <summary>Reports a map unlocked here. The host applies it and passes it on.</summary>
        /// <param name="guid">The map definition's guid.</param>
        private static void ReportMapUnlock(string guid)
        {
            if (!CoopSession.SharesWorld) return;
            if (string.IsNullOrEmpty(guid)) return;

            NetworkAccess.SendToAll(new MapUnlockMessage { Guid = guid });
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (!loaded) Reset();
        }
    }
}
