using Drova_Modding_API;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Il2CppDrova;
using Il2CppDrova.Daytime;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Stops a client from turning its own world over. The host's clock is the only clock.
    ///
    /// What this prevents is not a cosmetic disagreement about the time. Advancing the day without the
    /// saving flag runs the game's whole day-forward cascade - NPCs port to their routine places,
    /// day-scoped objects are destroyed, and the observers that watch for a new day write global
    /// variables, which this mod then faithfully sends to the host. A moment later
    /// <see cref="DaytimeSync"/> notices the client is ahead and pulls the number back. The number has
    /// an inverse; none of the rest of it does. So a client sleeping in an ordinary bed leaves its own
    /// world staged for tomorrow, its clock on yesterday, and the host's savegame carrying flags for a
    /// day nobody lived. It never logged a thing.
    ///
    /// The refusal stands, and a refused SLEEP is passed on rather than simply swallowed. Refusing
    /// alone was correct about the divergence and left the mine's bed unusable from the far end of a
    /// session: the sleep scene plays out, the day's flags cross, and neither world ever wakes up
    /// tomorrow - which is a teammate standing at a bed being told they still have work to do, for a
    /// day that ended.
    ///
    /// So a jump big enough to be somebody sleeping is reported to the host, which turns its own world
    /// over if it agrees. Nothing is granted back and nothing here waits: the host's clock report
    /// crosses periodically anyway, and a leap that size is already followed as a sleep by every
    /// client. That is what makes this survive a queue which keeps only the newest message - the
    /// earlier design put the grant itself on that queue, where one ordinary report arriving in
    /// between turned the followed sleep back into a silent correction.
    ///
    /// A small write is still only refused. Drift, a nudge, a scripted minute - none of those are a
    /// night, and none of them are worth another world's day.
    ///
    /// No <c>Update</c>: this has no per-frame work. It is deliberately not in <c>Core.Update</c>, so
    /// nobody should add an empty call there.
    /// </summary>
    internal static class ClockGuard
    {
        private static double _largestRefused;

        /// <summary>Whether the guard is in place. For <c>coop_status</c>.</summary>
        internal static bool Hooked { get; private set; }

        /// <summary>How many local day advances were refused. For <c>coop_status</c>.</summary>
        internal static int Refused { get; private set; }

        /// <summary>The largest of them, in game hours. For <c>coop_status</c>.</summary>
        internal static double LargestRefusedHours => _largestRefused / 3600d;

        /// <summary>How many of those were sleeps the host was asked to run. For <c>coop_status</c>.</summary>
        internal static int Asked { get; private set; }

        /// <summary>
        /// Whether the guard refuses anything - the kill switch, so a session in which this gets in
        /// the way can be finished rather than restarted.
        /// </summary>
        internal static bool Enabled { get; set; } = true;

        /// <summary>Forgets the counters. Called when a session ends and after a savegame load.</summary>
        internal static void Reset()
        {
            Refused = 0;
            Asked = 0;
            _largestRefused = 0d;
        }

        /// <summary>
        /// Applies the patch. Called once at melon init.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            // Overloaded on (int, int, bool) as well, so it is resolved by signature with an explicit
            // label. One patch covers every writer in the game: the hour-and-minute overload and the
            // percentage one both build a DrovaTime and delegate to this.
            System.Reflection.MethodBase? clock = HarmonyLib.AccessTools.Method(
                typeof(DaytimeGameHandler),
                nameof(DaytimeGameHandler.SetDayTime),
                new[] { typeof(DrovaTime), typeof(bool) });

            const string Label = "DaytimeGameHandler.SetDayTime(DrovaTime, bool)";

            Hooked = Hooking.TryPrefix(harmony, clock, typeof(ClockGuard), nameof(ClockWritePrefix), Label);

            if (!Hooked)
            {
                MelonLogger.Warning("[Clock] the clock guard did not install. A player sleeping in a " +
                    "bed on a client will move that world's day alone, fire the whole day-forward " +
                    "cascade, push its global-variable writes into the host's save, and then be " +
                    "silently dragged back.");
            }
        }

        /// <returns>False to refuse the write outright.</returns>
        private static bool ClockWritePrefix(DaytimeGameHandler __instance, DrovaTime newDayTime, bool bySaving)
        {
            // Everything that is not a client turning its own world over goes straight through:
            // loading a savegame, this mod's own silent drift correction, and the host's clock in
            // every mode including single player.
            if (bySaving) return true;
            if (!Enabled) return true;
            if (CoopSession.State != CoopSessionState.Joined) return true;

            // The one writer on a client that is allowed to run the day-forward path, because what it
            // is following is the host's own turnover. See DaytimeScope - without this test the guard
            // eats the host's sleep and clients quietly stop following it.
            if (RemoteApply<DaytimeScope>.InProgress) return true;

            try
            {
                double now = __instance.GetTime().GetTotalSeconds();
                double delta = newDayTime.GetTotalSeconds() - now;

                Refused++;
                if (System.Math.Abs(delta) > System.Math.Abs(_largestRefused)) _largestRefused = delta;

                if (delta >= DaytimeSync.SleepSeconds)
                {
                    Asked++;

                    CoopSession.RequestDayAdvance(newDayTime.GetTotalSeconds());

                    MelonLogger.Msg($"[Clock] this player slept - {delta / 3600d:0.#} game hours. This " +
                        "world does not turn itself over, so the host was asked to sleep for the " +
                        "session; when its clock leaps, this world follows it like any other night.");
                }
                else
                {
                    MelonLogger.Warning($"[Clock] something here tried to move this world's day by " +
                        $"{delta / 3600d:0.#} game hours. Only the host's clock turns the world over - a " +
                        "day advanced here alone fires every routine port and every day-forward flag in " +
                        "this world, sends those flags to the host, and is then pulled back with no way " +
                        "to undo them. Refused - and too small to be a night, so nobody was asked to " +
                        "sleep for it.");
                }
            }
            catch (System.Exception e)
            {
                MelonLogger.Error("[Clock] refusing a local day advance failed: " + e);
            }

            // Fails CLOSED, unlike almost everything else in this mod. Letting the write through on an
            // error is precisely the bug this exists to prevent.
            return false;
        }
    }
}
