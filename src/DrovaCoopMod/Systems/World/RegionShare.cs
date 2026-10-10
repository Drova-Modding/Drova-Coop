using Drova_Modding_API.Access;
using Drova_Modding_API.Systems;
using Drova_Modding_API.Systems.WorldEvents;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.Session;
using MelonLoader;

namespace DrovaCoopMod.Systems.World
{
    /// <summary>
    /// Feeds clients' regions into the host's region coordinator, so host-rolled regional events spawn around teammates too.
    /// </summary>
    internal sealed class RegionShare : ICoopSystem
    {
        // A load moves a client without leave events, and the host's coordinator is rebuilt per world, so regions are restated.
        private const float RestateSeconds = 10f;

        private static readonly List<Region> Current = new();
        private static readonly HashSet<Region> Told = new();
        private static readonly HashSet<PlayerId> Reported = new();
        private static bool _subscribed;
        private static float _sinceRestated = RestateSeconds;

        string? ICoopSystem.HookSwitch => null;

        /// <summary>Region changes a client reported here (host). For tests.</summary>
        internal static int Taken { get; private set; }

        /// <summary>Listens for this player's region changes.</summary>
        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            if (_subscribed) return;

            RegionAccess.OnRegionChanged += OnRegionChanged;
            _subscribed = true;
        }

        /// <summary>Client: restates this player's regions to the host. Host: forgets teammates who left.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (CoopSession.JoinedWorld)
            {
                _sinceRestated += unscaledDeltaTime;
                if (_sinceRestated < RestateSeconds) return;
                _sinceRestated = 0f;

                Current.Clear();
                RegionAccess.GetCurrentRegions(Current);
                foreach (Region region in Told.ToList())
                {
                    if (Current.Contains(region)) continue;
                    Send(region, false);
                    Told.Remove(region);
                }

                foreach (Region region in Current)
                {
                    Send(region, true);
                    Told.Add(region);
                }

                return;
            }

            Told.Clear();

            if (!CoopSession.HostsWorld || Reported.Count == 0) return;

            foreach (PlayerId player in Reported.ToList())
            {
                if (RemotePlayers.Bodies.ContainsKey(player)) continue;

                WorldEventSystemManager.Instance?.ForgetRemotePlayer(player.Value);
                Reported.Remove(player);
            }
        }

        /// <summary>Host: a client's region change, into the region coordinator.</summary>
        private static void Take(PlayerId sender, in RegionMessage message)
        {
            try
            {
                if (!Enum.IsDefined(typeof(Region), (int)message.Region)) return;

                WorldEventSystemManager? manager = WorldEventSystemManager.Instance;
                if (manager == null) return;

                manager.ReportRemoteRegion(sender.Value, (Region)message.Region, message.Entered != 0,
                    () => RemotePlayers.Bodies.TryGetValue(sender, out RemotePlayer body) && body.Body != null ? body.Body.GetFeetPosition() : null);
                Reported.Add(sender);
                Taken++;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Region] taking a teammate's region change failed: " + e);
            }
        }

        /// <summary>Forgets everything. Called when a world or session ends.</summary>
        private static void Clear()
        {
            foreach (PlayerId player in Reported) WorldEventSystemManager.Instance?.ForgetRemotePlayer(player.Value);
            Reported.Clear();

            // Told stays, so a region left by loading is taken back at the restate.
            _sinceRestated = RestateSeconds;
        }

        private static void OnRegionChanged(Region region, bool hasEntered)
        {
            if (!CoopSession.JoinedWorld) return;

            Send(region, hasEntered);
            if (hasEntered) Told.Add(region);
            else Told.Remove(region);
        }

        private static void Send(Region region, bool entered)
        {
            ReportRegion(new RegionMessage { Region = (byte)region, Entered = (byte)(entered ? 1 : 0) });
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<RegionMessage>(MessageIds.Region, (peer, m) =>
            {
                if (CoopSession.HostsWorld && CoopSession.TryGetSender(peer, out PlayerId sender)) Take(sender, m);
            });
        }

        /// <summary>This client's player entered or left a region: to the host.</summary>
        private static void ReportRegion(in RegionMessage message)
        {
            if (CoopSession.JoinedWorld) NetworkAccess.SendToAll(message);
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings) => Clear();
    }
}
