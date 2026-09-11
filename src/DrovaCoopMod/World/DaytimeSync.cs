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

        /// <summary>
        /// The most a teammate's ask may move this world, in game seconds. A night is under half of
        /// this; anything beyond it is a number nobody meant, and a day advanced cannot be taken back.
        /// </summary>
        private const double MaxGrantSeconds = 86400d;

        /// <summary>
        /// How long this world waits before sleeping for somebody again, in real seconds. Two players
        /// going to bed at the same moment is one night, not two.
        /// </summary>
        private const float GrantCooldownSeconds = 20f;

        /// <summary>
        /// A backward correction at least this large, in game seconds, is worth a line.
        ///
        /// Because it is never ordinary drift. This world being an hour ahead of the host's means
        /// something here moved the clock forward on its own, and moving the clock forward is the
        /// least of what that did.
        /// </summary>
        private const double RewindNoticeSeconds = 3600d;

        private static float _sinceSend;
        private static bool _caughtUp;
        private static int _followedSleeps;
        private static float _sinceGrant = GrantCooldownSeconds;
        private static int _granted;
        private static int _refusedAsks;
        private static int _rewinds;
        private static double _largestRewind;

        /// <summary>How many host sleeps this world followed. For <c>coop_status</c>.</summary>
        internal static int FollowedSleeps => _followedSleeps;

        /// <summary>How many teammate sleeps this world ran for them. For <c>coop_status</c>.</summary>
        internal static int Granted => _granted;

        /// <summary>How many were turned down. For <c>coop_status</c>.</summary>
        internal static int RefusedAsks => _refusedAsks;

        /// <summary>How often this world was found ahead of the host's and pulled back.</summary>
        internal static int Rewinds => _rewinds;

        /// <summary>The largest of those, in game hours. For <c>coop_status</c>.</summary>
        internal static double LargestRewindHours => _largestRewind / 3600d;

        /// <summary>Forgets the state. Called when a session ends.</summary>
        internal static void Reset()
        {
            _sinceSend = 0f;
            _caughtUp = false;
            _followedSleeps = 0;
            _sinceGrant = GrantCooldownSeconds;
            _granted = 0;
            _refusedAsks = 0;
            _rewinds = 0;
            _largestRewind = 0d;
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

        /// <summary>
        /// Sleeps on a teammate's behalf, when they ask.
        ///
        /// A client may not turn its own world over, so a player sleeping at the far end of a session
        /// reports the night they were trying to have and this runs it here - the game's own
        /// day-forward, on the one clock the session has. The report below then carries the new time
        /// out, and every world including theirs follows it as a sleep.
        ///
        /// The ask is checked rather than obeyed, because a day cannot be un-advanced:
        /// <list type="bullet">
        /// <item>forward and night-sized, so a stale ask that this world has already slept past does
        /// nothing;</item>
        /// <item>no further than a single day, so one wrong number cannot skip a week;</item>
        /// <item>and not again within the cooldown, so two players going to bed together - or one
        /// client repeating itself - is one night rather than two.</item>
        /// </list>
        /// The cooldown is checked before the queue is touched, so a request that arrives too soon
        /// waits its turn instead of being thrown away: dropping it would lose the night entirely.
        /// </summary>
        private static void Grant(float unscaledDeltaTime)
        {
            _sinceGrant += unscaledDeltaTime;
            if (_sinceGrant < GrantCooldownSeconds) return;

            if (!CoopSession.TryTakeDayAdvance(out Messages.DayAdvanceRequestMessage ask)) return;

            try
            {
                if (!DaytimeGameHandler.TryGet(out DaytimeGameHandler handler) || handler == null) return;

                double now = handler.GetTime().GetTotalSeconds();
                double delta = ask.TargetTotalSeconds - now;

                if (delta < SleepSeconds || delta > MaxGrantSeconds)
                {
                    _refusedAsks++;

                    MelonLogger.Warning($"[Time] player {ask.Player} asked this world to move on " +
                        $"{delta / 3600d:0.#} game hours and it was turned down. Either this world has " +
                        "already slept past what they asked for, or the number is not a night. Their " +
                        "clock still follows this one, so nothing is lost but the sleep.");
                    return;
                }

                DrovaTime target = new(ask.TargetTotalSeconds);

                // Not bySaving, on purpose: this is a real night and the whole day-forward has to run
                // here, exactly as it would have if the person sleeping were sitting at this machine.
                handler.SetDayTime(target, bySaving: false);

                _sinceGrant = 0f;
                _granted++;

                MelonLogger.Msg($"[Time] player {ask.Player} slept, so this world slept with them - " +
                    $"{delta / 3600d:0.#} game hours forward. The clock report carries the night to " +
                    "everybody, including back to them.");

                // Straight away rather than at the next tick of the ordinary report, because the
                // player who asked is standing in a world that has not woken up yet.
                if (TryBuildCurrent(out DaytimeMessage message))
                {
                    CoopSession.BroadcastDaytime(message);
                    _sinceSend = 0f;
                }
            }
            catch (System.Exception e)
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

                // Loud when THIS world is the one that is ahead. A silent rewind is how a client's own
                // bed sleep used to be erased without leaving a mark anywhere: the day-forward cascade
                // had already run here and nowhere else, and pulling the number back undoes none of
                // it. With the clock guard in place this must never fire; if it does, something in
                // this world still writes the clock behind the guard's back and wants finding.
                if (delta <= -RewindNoticeSeconds)
                {
                    _rewinds++;
                    if (-delta > _largestRewind) _largestRewind = -delta;

                    MelonLogger.Warning($"[Time] this world's clock is {-delta / 3600d:0.#} game hours " +
                        "AHEAD of the host's and is being pulled back. Whatever put it there ran the " +
                        "whole day-forward cascade here and nowhere else, and pulling the number back " +
                        "does not undo any of it.");
                }

                // The one non-saving clock write a client is allowed to make. See DaytimeScope.
                using (Sync.RemoteApply<Sync.DaytimeScope>.Enter())
                {
                    handler.SetDayTime(target, bySaving: !sleepForward);
                }

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
