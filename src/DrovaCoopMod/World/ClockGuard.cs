using Drova_Modding_API;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Il2CppDrova;
using Il2CppDrova.Daytime;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Refuses a client's own day advances, since the day-forward cascade has no inverse. The host's clock is the only clock.
    /// A sleep-sized jump or a dialogue's time change is passed to the host, and this world follows its clock report.
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

        /// <summary>Kill switch for the guard.</summary>
        internal static bool Enabled { get; set; } = true;

        /// <summary>Forgets the counters. Called when a session ends and after a savegame load.</summary>
        internal static void Reset()
        {
            Refused = 0;
            Asked = 0;
            _largestRefused = 0d;
        }

        // Set while a dialogue node writes the time, so it is told apart from a bed.
        private static bool _scripted;

        private static void ScriptedPrefix()
        {
            _scripted = true;
        }

        private static void ScriptedPostfix()
        {
            _scripted = false;
        }

        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            // The other overloads all delegate to this one, so it covers every writer.
            System.Reflection.MethodBase? clock = HarmonyLib.AccessTools.Method(
                typeof(DaytimeGameHandler),
                nameof(DaytimeGameHandler.SetDayTime),
                new[] { typeof(DrovaTime), typeof(bool) });

            const string label = "DaytimeGameHandler.SetDayTime(DrovaTime, bool)";

            Hooked = Hooking.TryPrefix(harmony, clock, typeof(ClockGuard), nameof(ClockWritePrefix), label);

            Hooking.TryPrefix(harmony, typeof(Il2CppDrova.DialogueNew.DS_SetDayTimeNode), "OnExecute",
                typeof(ClockGuard), nameof(ScriptedPrefix));
            Hooking.TryPostfix(harmony, typeof(Il2CppDrova.DialogueNew.DS_SetDayTimeNode), "OnExecute",
                typeof(ClockGuard), nameof(ScriptedPostfix));

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
            // Save loads and drift corrections pass with bySaving.
            if (bySaving) return true;
            if (!Enabled) return true;
            if (CoopSession.State != CoopSessionState.Joined) return true;

            // Following the host's own turnover.
            if (RemoteApply<DaytimeScope>.InProgress) return true;

            try
            {
                double now = __instance.GetTime().GetTotalSeconds();
                double delta = newDayTime.GetTotalSeconds() - now;

                Refused++;
                if (Math.Abs(delta) > Math.Abs(_largestRefused)) _largestRefused = delta;

                bool scripted = _scripted;
                _scripted = false;

                if (scripted && delta != 0d)
                {
                    Asked++;

                    // A story time change (Helma's fire festival, Eugen's camp) is made by the host.
                    CoopSession.RequestDayAdvance(newDayTime.GetTotalSeconds(), scripted: true);

                    MelonLogger.Msg($"[Clock] a dialogue set the time {delta / 3600d:+0.#;-0.#} game hours; " +
                        "the host was asked to set it for the session.");
                }
                else if (delta >= DaytimeSync.SleepSeconds)
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
            catch (Exception e)
            {
                MelonLogger.Error("[Clock] refusing a local day advance failed: " + e);
            }

            // Fails closed: letting the write through is the bug this prevents.
            return false;
        }
    }
}
