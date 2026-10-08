using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using Il2CppDrova.Daytime;
using MelonLoader;

namespace DrovaCoopMod.Systems.World
{
    /// <summary>
    /// Keeps every world on the host's clock: drift is corrected silently, a leap forward is followed as a sleep.
    /// </summary>
    internal sealed class DaytimeSync : ICoopSystem
    {
        /// <summary>How often the host reports, in real seconds.</summary>
        private const float SendSeconds = 5f;

        /// <summary>Drift below this stands, in game seconds, so the light does not stutter.</summary>
        private const double DriftSeconds = 300d;

        /// <summary>
        /// A forward jump at least this large is a sleep, in game seconds. <see cref="ClockGuard"/> uses the same figure.
        /// </summary>
        internal const double SleepSeconds = 7200d;

        /// <summary>The most a teammate's ask may move this world, in game seconds, since a day advanced cannot be taken back.</summary>
        private const double MaxGrantSeconds = 86400d;

        /// <summary>The smallest scripted time change granted: under it, both worlds already agree.</summary>
        private const double ScriptedMinimumSeconds = 1800d;

        /// <summary>Real seconds before sleeping for somebody again, so two players going to bed together is one night.</summary>
        private const float GrantCooldownSeconds = 20f;

        /// <summary>A backward correction at least this large, in game seconds, is never ordinary drift and is logged.</summary>
        private const double RewindNoticeSeconds = 3600d;

        private static float _sinceSend;
        private static bool _caughtUp;
        private static float _sinceGrant = GrantCooldownSeconds;
        private static double _largestRewind;

        private static readonly Queue<DaytimeMessage> IncomingDaytime = new();

        // Host only: clients asking for the day to turn over because they slept.
        private static readonly Queue<DayAdvanceRequestMessage> IncomingDayAdvance = new();

        /// <summary>How many host sleeps this world followed. For <c>coop_status</c>.</summary>
        internal static int FollowedSleeps { get; private set; }

        /// <summary>How many teammate sleeps this world ran for them. For <c>coop_status</c>.</summary>
        internal static int Granted { get; private set; }

        /// <summary>How many were turned down. For <c>coop_status</c>.</summary>
        internal static int RefusedAsks { get; private set; }

        /// <summary>How often this world was found ahead of the host's and pulled back.</summary>
        internal static int Rewinds { get; private set; }

        /// <summary>The largest of those, in game hours. For <c>coop_status</c>.</summary>
        internal static double LargestRewindHours => _largestRewind / 3600d;

        /// <summary>Forgets the state. Called when a session ends.</summary>
        private static void Reset()
        {
            _sinceSend = 0f;
            _caughtUp = false;
            FollowedSleeps = 0;
            _sinceGrant = GrantCooldownSeconds;
            Granted = 0;
            RefusedAsks = 0;
            Rewinds = 0;
            _largestRewind = 0d;
        }

        /// <summary>Reports or follows, whichever this machine is.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            switch (CoopSession.State)
            {
                case CoopSessionState.Hosting:
                    Grant(unscaledDeltaTime);
                    Report(unscaledDeltaTime);
                    return;

                case CoopSessionState.Joined:
                    Follow();
                    return;

                default:
                    Reset();
                    return;
            }
        }

        /// <summary>Reads the clock, for the report a joining player gets with the handshake.</summary>
        /// <returns>False when the time system is not up.</returns>
        internal static bool TryBuildCurrent(out DaytimeMessage message)
        {
            message = default;

            if (!DaytimeGameHandler.TryGet(out DaytimeGameHandler handler) || handler == null) return false;

            message = new DaytimeMessage { TotalSeconds = handler.GetTime().GetTotalSeconds() };
            return true;
        }

        /// <summary>
        /// Runs a client's sleep on the host clock, checked against size and cooldown since a day cannot be un-advanced.
        /// The cooldown is checked before dequeuing, so an early ask waits instead of being lost.
        /// </summary>
        private static void Grant(float unscaledDeltaTime)
        {
            _sinceGrant += unscaledDeltaTime;
            if (_sinceGrant < GrantCooldownSeconds) return;

            if (!TryTakeDayAdvance(out DayAdvanceRequestMessage ask)) return;

            try
            {
                if (!DaytimeGameHandler.TryGet(out DaytimeGameHandler handler) || handler == null) return;

                double now = handler.GetTime().GetTotalSeconds();
                double delta = ask.TargetTotalSeconds - now;

                // A dialogue's time set is granted as authored, backwards included.
                // A small one is the evening this world already set, and a second write would run day-forward twice.
                bool refused = ask.Scripted
                    ? Math.Abs(delta) < ScriptedMinimumSeconds || Math.Abs(delta) > MaxGrantSeconds
                    : delta < SleepSeconds || delta > MaxGrantSeconds;

                if (refused)
                {
                    RefusedAsks++;

                    MelonLogger.Warning($"[Time] player {ask.Player} asked this world to move on " +
                        $"{delta / 3600d:0.#} game hours and it was turned down. Either this world has " +
                        "already slept past what they asked for, or the number is not a night. Their " +
                        "clock still follows this one, so nothing is lost but the sleep.");
                    return;
                }

                DrovaTime target = new(ask.TargetTotalSeconds);

                // Not bySaving: this is a real night and the whole day-forward has to run.
                handler.SetDayTime(target, bySaving: false);

                _sinceGrant = 0f;
                Granted++;

                MelonLogger.Msg($"[Time] player {ask.Player} slept, so this world slept with them - " +
                    $"{delta / 3600d:0.#} game hours forward. The clock report carries the night to " +
                    "everybody, including back to them.");

                // Straight away, since the player who asked is waiting in a world that has not woken up yet.
                if (TryBuildCurrent(out DaytimeMessage message))
                {
                    BroadcastDaytime(message);
                    _sinceSend = 0f;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Time] sleeping for a teammate failed: " + e);
            }
        }

        private static void Report(float unscaledDeltaTime)
        {
            _sinceSend += unscaledDeltaTime;
            if (_sinceSend < SendSeconds) return;

            _sinceSend = 0f;

            if (TryBuildCurrent(out DaytimeMessage message))
            {
                BroadcastDaytime(message);
            }
        }

        private static void Follow()
        {
            bool any = false;
            DaytimeMessage latest = default;

            while (TryTakeDaytime(out DaytimeMessage message))
            {
                latest = message;
                any = true;
            }

            if (!any) return;

            try
            {
                if (!DaytimeGameHandler.TryGet(out DaytimeGameHandler handler) || handler == null) return;

                double local = handler.GetTime().GetTotalSeconds();
                double delta = latest.TotalSeconds - local;

                // The first report after joining is a catch-up, never a sleep, however large the gap.
                bool first = !_caughtUp;
                _caughtUp = true;

                if (Math.Abs(delta) < DriftSeconds) return;

                DrovaTime target = new(latest.TotalSeconds);

                bool sleepForward = !first && delta >= SleepSeconds;

                // With ClockGuard in place this never fires. If it does, something here writes the clock behind its back.
                if (delta <= -RewindNoticeSeconds)
                {
                    Rewinds++;
                    if (-delta > _largestRewind) _largestRewind = -delta;

                    MelonLogger.Warning($"[Time] this world's clock is {-delta / 3600d:0.#} game hours " +
                        "AHEAD of the host's and is being pulled back. Whatever put it there ran the " +
                        "whole day-forward cascade here and nowhere else, and pulling the number back " +
                        "does not undo any of it.");
                }

                // The one non-saving clock write a client is allowed to make. See DaytimeScope.
                using (Sync.RemoteApply<DaytimeScope>.Enter())
                {
                    handler.SetDayTime(target, bySaving: !sleepForward);
                }

                if (sleepForward)
                {
                    FollowedSleeps++;
                    MelonLogger.Msg($"[Time] the host's clock leapt {delta / 3600d:0.#} game hours " +
                        "ahead - they slept - and this world slept along.");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Time] following the host's clock failed: " + e);
            }
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<DayAdvanceRequestMessage>(MessageIds.DayAdvanceRequest, OnDayAdvanceRequest);
            CoopSession.Register<DaytimeMessage>(MessageIds.Daytime, OnDaytime);
        }

        void ICoopSystem.EndSession()
        {
            IncomingDaytime.Clear();
            IncomingDayAdvance.Clear();
        }

        /// <summary>Takes the next clock report from the host.</summary>
        private static bool TryTakeDaytime(out DaytimeMessage message)
            => IncomingDaytime.TryDequeue(out message);

        /// <summary>Tells every client what time it is. Host only.</summary>
        private static void BroadcastDaytime(in DaytimeMessage message)
            => CoopSession.SendIfHost(message);

        private static void OnDaytime(INetPeer peer, DaytimeMessage message)
        {
            if (!CoopSession.IsFromHost()) return;

            CoopSession.EnqueueLatest(IncomingDaytime, message, 4);
        }

        /// <summary>
        /// Asks the host to turn the world over because this player slept. Client only. The host decides, and the
        /// answer is the ordinary clock report.
        /// </summary>
        /// <param name="targetTotalSeconds">The time this player was trying to wake up at.</param>
        /// <param name="scripted">Whether a dialogue set it rather than a bed.</param>
        internal static void RequestDayAdvance(double targetTotalSeconds, bool scripted = false)
        {
            if (CoopSession.State != CoopSessionState.Joined) return;

            NetworkAccess.SendToAll(new DayAdvanceRequestMessage
            {
                Player = CoopSession.LocalPlayerId,
                TargetTotalSeconds = targetTotalSeconds,
                Scripted = scripted,
            });
        }

        /// <summary>Takes the next day advance a client asked for. Host only.</summary>
        private static bool TryTakeDayAdvance(out DayAdvanceRequestMessage message)
            => IncomingDayAdvance.TryDequeue(out message);

        private static void OnDayAdvanceRequest(INetPeer peer, DayAdvanceRequestMessage message)
        {
            // Only the host turns a world over.
            if (CoopSession.State != CoopSessionState.Hosting) return;

            if (!message.IsPlausible()) return;

            if (!CoopSession.TryGetSender(peer, out PlayerId sender)) return;

            message.Player = sender;

            // Keep the oldest: dropping one would skip a night, not delay it. The host's cooldown absorbs repeats.
            CoopSession.Enqueue(IncomingDayAdvance, message, 4);
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (!loaded) Reset();
        }
    }
}
