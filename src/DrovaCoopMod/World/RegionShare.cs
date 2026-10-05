using Drova_Modding_API;
using Drova_Modding_API.Access;
using Drova_Modding_API.Systems;
using Drova_Modding_API.Systems.WorldEvents;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Regional events for every player, rolled by the host.
    ///
    /// A region's ambush is world state, so only the host rolls it (RandomEvents' authority gate) - and the host's region
    /// tracking sees only the host's own player, so a teammate walking into a region alone got nothing. Each client now
    /// reports its regions; the host feeds them into the API's region coordinator with the teammate's body as the spot,
    /// and the ambush spawns around them and is shared like any spawn.
    /// </summary>
    internal static class RegionShare
    {
        // A client restates its regions this often: a load moves it without leave events, and the host's region coordinator
        // is rebuilt with every world the host loads. Entering a region the host already has it in changes nothing there.
        private const float RestateSeconds = 10f;

        private static readonly List<Region> _current = new();
        private static readonly HashSet<Region> _told = new();
        private static readonly HashSet<PlayerId> _reported = new();
        private static bool _subscribed;
        private static float _sinceRestated = RestateSeconds;

        /// <summary>Region changes a client reported here (host). For tests.</summary>
        internal static int Taken { get; private set; }

        /// <summary>Listens for this player's region changes. Called once at melon init.</summary>
        internal static void Initialize()
        {
            if (_subscribed) return;

            RegionAccess.OnRegionChanged += OnRegionChanged;
            _subscribed = true;
        }

        /// <summary>Client: restates this player's regions to the host. Host: forgets teammates who left. Once per frame.</summary>
        internal static void Update(float unscaledDeltaTime)
        {
            if (CoopSession.State == CoopSessionState.Joined)
            {
                _sinceRestated += unscaledDeltaTime;
                if (_sinceRestated < RestateSeconds) return;
                _sinceRestated = 0f;

                _current.Clear();
                RegionAccess.GetCurrentRegions(_current);
                foreach (Region region in _told.ToList())
                {
                    if (_current.Contains(region)) continue;
                    Send(region, false);
                    _told.Remove(region);
                }

                foreach (Region region in _current)
                {
                    Send(region, true);
                    _told.Add(region);
                }

                return;
            }

            _told.Clear();

            if (CoopSession.State != CoopSessionState.Hosting || _reported.Count == 0) return;

            foreach (PlayerId player in _reported.ToList())
            {
                if (RemotePlayers.Bodies.ContainsKey(player)) continue;

                WorldEventSystemManager.Instance?.ForgetRemotePlayer(player.Value);
                _reported.Remove(player);
            }
        }

        /// <summary>Host: a client's region change, into the region coordinator.</summary>
        internal static void Take(PlayerId sender, in RegionMessage message)
        {
            try
            {
                if (!Enum.IsDefined(typeof(Region), (int)message.Region)) return;

                WorldEventSystemManager? manager = WorldEventSystemManager.Instance;
                if (manager == null) return;

                manager.ReportRemoteRegion(sender.Value, (Region)message.Region, message.Entered != 0,
                    () => RemotePlayers.Bodies.TryGetValue(sender, out RemotePlayer? body) && body.Body != null ? body.Body.GetFeetPosition() : null);
                _reported.Add(sender);
                Taken++;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Region] taking a teammate's region change failed: " + e);
            }
        }

        /// <summary>Forgets everything. Called when a world or session ends.</summary>
        internal static void Clear()
        {
            foreach (PlayerId player in _reported) WorldEventSystemManager.Instance?.ForgetRemotePlayer(player.Value);
            _reported.Clear();

            // Restated soon after a load; what the host was told stays known, so a region left by loading is taken back.
            _sinceRestated = RestateSeconds;
        }

        private static void OnRegionChanged(Region region, bool hasEntered)
        {
            if (CoopSession.State != CoopSessionState.Joined) return;

            Send(region, hasEntered);
            if (hasEntered) _told.Add(region);
            else _told.Remove(region);
        }

        private static void Send(Region region, bool entered)
        {
            CoopSession.ReportRegion(new RegionMessage { Region = (byte)region, Entered = (byte)(entered ? 1 : 0) });
        }
    }
}
