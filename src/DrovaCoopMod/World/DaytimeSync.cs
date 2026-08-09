using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.Daytime;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Keeps every world living through the same day.
    ///
    /// Each machine's clock advances on its own and sleeping jumps it by hours, so two worlds drift
    /// into different days - and everything staged by day and daytime drifts with them, which is
    /// fatal to story content that advances by sleeping. The host's clock crosses periodically.
    /// Drift is corrected silently through the save-restore path; a leap forward is followed with
    /// the game's own day-forward semantics, so when the host sleeps, this world sleeps along -
    /// routines port, the world restocks, the same morning arrives everywhere.
    /// </summary>
    internal static class DaytimeSync
    {
        /// <summary>How often the host reports, in real seconds.</summary>
        private const float SendSeconds = 5f;

        /// <summary>Drift below this stands, in game seconds. Corrections chasing seconds would
        /// visibly stutter the light for nothing.</summary>
        private const double DriftSeconds = 300d;

        /// <summary>
        /// A forward jump at least this large is a sleep, in game seconds, and is followed loudly
        /// with the game's own day-forward path. Below it, the clock is nudged silently.
        /// </summary>
        private const double SleepSeconds = 7200d;

        private static float _sinceSend;
        private static bool _caughtUp;
        private static int _followedSleeps;

        /// <summary>How many host sleeps this world followed. For <c>coop_status</c>.</summary>
        internal static int FollowedSleeps => _followedSleeps;

        /// <summary>Forgets the state. Called when a session ends.</summary>
        internal static void Reset()
        {
            _sinceSend = 0f;
            _caughtUp = false;
            _followedSleeps = 0;
        }

        /// <summary>
        /// Reports or follows, whichever this machine is. Call once per frame from a loaded world.
        /// </summary>
        /// <param name="unscaledDeltaTime">Seconds since the last frame.</param>
        internal static void Update(float unscaledDeltaTime)
        {
            switch (CoopSession.State)
            {
                case CoopSessionState.Hosting:
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

        /// <summary>
        /// Reads the clock, for the report a joining player gets with the handshake.
        /// </summary>
        /// <param name="message">The report.</param>
        /// <returns>False when the time system is not up.</returns>
        internal static bool TryBuildCurrent(out DaytimeMessage message)
        {
            message = default;

            if (!DaytimeGameHandler.TryGet(out DaytimeGameHandler handler) || handler == null) return false;

            message = new DaytimeMessage { TotalSeconds = handler.GetTime().GetTotalSeconds() };
            return true;
        }

        private static void Report(float unscaledDeltaTime)
        {
            _sinceSend += unscaledDeltaTime;
            if (_sinceSend < SendSeconds) return;

            _sinceSend = 0f;

            if (TryBuildCurrent(out DaytimeMessage message))
            {
                CoopSession.BroadcastDaytime(message);
            }
        }

        private static void Follow()
        {
            bool any = false;
            DaytimeMessage latest = default;

            while (CoopSession.TryTakeDaytime(out DaytimeMessage message))
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

                // The first report after joining is history, not an event, however large the gap is -
                // the world just loaded, and replaying a day-forward over it helps nobody.
                bool first = !_caughtUp;
                _caughtUp = true;

                if (System.Math.Abs(delta) < DriftSeconds) return;

                DrovaTime target = new(latest.TotalSeconds);

                // A leap forward is a sleep and is followed with the game's own day-forward
                // semantics. Drift, backward corrections and the join catch-up move the clock
                // silently, which no sleep ever does.
                bool sleepForward = !first && delta >= SleepSeconds;

                handler.SetDayTime(target, bySaving: !sleepForward);

                if (sleepForward)
                {
                    _followedSleeps++;
                    MelonLogger.Msg($"[Time] the host's clock leapt {delta / 3600d:0.#} game hours " +
                        "ahead - they slept - and this world slept along.");
                }
            }
            catch (System.Exception e)
            {
                MelonLogger.Error("[Time] following the host's clock failed: " + e);
            }
        }
    }
}
