using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova.Weather;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Puts every machine under the host's sky. The host sends its weather parameters, and a client
    /// freezes its own updater, which rerolls hourly, and glides toward them.
    /// </summary>
    internal static class WeatherSync
    {
        /// <summary>How often the host reports, when something moved.</summary>
        private const float SendSeconds = 2f;

        /// <summary>How often the host reports regardless, so a drop is healed.</summary>
        private const float KeepaliveSeconds = 15f;

        private const float Epsilon = 0.001f;

        /// <summary>How long a client glides onto reported values, so the sky does not jump per report.</summary>
        private const float FollowSeconds = 3f;

        private static readonly float[] LastSent = new float[WeatherMessage.ParamCount];
        private static readonly float[] Target = new float[WeatherMessage.ParamCount];

        private static bool _hasTarget;
        private static bool _frozen;
        private static float _sinceSend;
        private static float _sinceForced;

        /// <summary>Lets go of the sky. Called when a session ends.</summary>
        internal static void Reset()
        {
            if (_frozen)
            {
                _frozen = false;

                if (TryGetUpdater(out WeatherStateUpdater updater))
                {
                    updater._isEnabled = true;
                }
            }

            _hasTarget = false;
            _sinceSend = 0f;
            _sinceForced = 0f;
        }

        /// <summary>Reports or follows, whichever this machine is. Call once per frame from a loaded world.</summary>
        internal static void Update(float unscaledDeltaTime)
        {
            switch (CoopSession.State)
            {
                case CoopSessionState.Hosting:
                    Report(unscaledDeltaTime);
                    ReportThunder();
                    return;

                case CoopSessionState.Joined:
                    Follow(unscaledDeltaTime);
                    HoldOwnThunder();
                    return;

                default:
                    Reset();
                    return;
            }
        }

        /// <summary>Reads the current parameters, for the handshake. False when the weather system is not up.</summary>
        internal static bool TryBuildCurrent(out WeatherMessage message)
        {
            message = default;

            if (!TryGetUpdater(out WeatherStateUpdater updater)) return false;

            Il2Cpp.WeatherState state = updater.CurState;
            if (state == null) return false;

            float[] values = new float[WeatherMessage.ParamCount];

            for (int index = 0; index < values.Length; index++)
            {
                values[index] = state.GetParamValue(index);
            }

            message = new WeatherMessage { Params = values };
            return true;
        }

        private static float _lastLightning;

        /// <summary>Strikes the host announced, and strikes a client played for the host. For tests.</summary>
        internal static int ThunderSent { get; private set; }

        /// <summary>See <see cref="ThunderSent"/>.</summary>
        internal static int ThunderShown { get; private set; }

        /// <summary>The storm's thunder effect, whose strikes each machine rolls on its own cooldown.</summary>
        private static WeatherEffect_Thunder? Thunder()
        {
            if (!WeatherGameHandler.TryGet(out WeatherGameHandler handler) || handler == null || handler._currentEffects == null) return null;

            foreach (AWeatherEffect effect in handler._currentEffects)
            {
                WeatherEffect_Thunder? thunder = effect?.TryCast<WeatherEffect_Thunder>();
                if (thunder != null) return thunder;
            }

            return null;
        }

        /// <summary>Host: a strike here (its flash time jumped) is a strike everywhere.</summary>
        private static void ReportThunder()
        {
            WeatherEffect_Thunder? thunder = Thunder();
            if (thunder == null) return;

            float lightning = thunder._curLightningTime;
            if (lightning > _lastLightning + 0.01f)
            {
                CoopSession.BroadcastThunder();
                ThunderSent++;
            }

            _lastLightning = lightning;
        }

        /// <summary>Client: its own storm rolls no strikes. A requested strike (cooldown -1) is left alone.</summary>
        private static void HoldOwnThunder()
        {
            WeatherEffect_Thunder? thunder = Thunder();
            if (thunder != null && thunder._strikeCooldown > 0f && thunder._strikeCooldown < 1e5f) thunder._strikeCooldown = 1e6f;
        }

        /// <summary>Client: the host's storm struck.</summary>
        internal static void TakeThunder()
        {
            try
            {
                WeatherEffect_Thunder? thunder = Thunder();
                if (thunder == null) return;

                thunder.TriggerStrikeNow();
                ThunderShown++;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Weather] playing the host's thunder strike failed: " + e);
            }
        }

        private static void Report(float unscaledDeltaTime)
        {
            _sinceSend += unscaledDeltaTime;
            _sinceForced += unscaledDeltaTime;

            if (_sinceSend < SendSeconds) return;

            _sinceSend = 0f;

            if (!TryBuildCurrent(out WeatherMessage message)) return;

            bool moved = false;

            for (int index = 0; index < message.Params.Length; index++)
            {
                if (Math.Abs(message.Params[index] - LastSent[index]) > Epsilon)
                {
                    moved = true;
                    break;
                }
            }

            if (!moved && _sinceForced < KeepaliveSeconds) return;

            _sinceForced = 0f;
            message.Params.CopyTo(LastSent, 0);
            CoopSession.BroadcastWeather(message);
        }

        private static void Follow(float unscaledDeltaTime)
        {
            while (CoopSession.TryTakeWeather(out WeatherMessage message))
            {
                if (message.Params == null) continue;

                message.Params.CopyTo(Target, 0);
                _hasTarget = true;
            }

            if (!_hasTarget) return;

            if (!TryGetUpdater(out WeatherStateUpdater updater)) return;

            // Checked every frame, since the game builds a new, enabled updater on a long teleport.
            if (updater._isEnabled)
            {
                _frozen = true;
                updater._isEnabled = false;
            }

            // While a lerp runs, SetWeatherParamLevel writes the lerp's target, not the CurState read below.
            if (updater.Lerper != null && updater.Lerper.IsLerping)
            {
                updater.Lerper.Cancel();
            }

            Il2Cpp.WeatherState state = updater.CurState;
            if (state == null) return;

            float step = unscaledDeltaTime / FollowSeconds;
            if (step > 1f) step = 1f;

            for (int index = 0; index < WeatherMessage.ParamCount; index++)
            {
                float current = state.GetParamValue(index);
                float next = current + (Target[index] - current) * step;

                // Land exactly on the host's value, since the foglet patches read levels.
                if (Math.Abs(Target[index] - current) < Epsilon)
                {
                    if (current == Target[index]) continue;
                    next = Target[index];
                }

                updater.SetWeatherParamLevel((WeatherParam)index, next);
            }
        }

        /// <summary>One line on this machine's half of the sync. For <c>coop_status</c>.</summary>
        internal static string Describe()
        {
            if (!TryGetUpdater(out WeatherStateUpdater updater))
            {
                return "weather: the weather system is not reachable here.";
            }

            Il2Cpp.WeatherState state = updater.CurState;
            string current = state == null
                ? "?"
                : string.Join("/", Enumerable.Range(0, WeatherMessage.ParamCount)
                    .Select(index => state.GetParamValue(index).ToString("0.00")));

            if (CoopSession.State == CoopSessionState.Hosting)
            {
                return $"weather: reporting, current {current}, last sent " +
                    string.Join("/", LastSent.Select(value => value.ToString("0.00"))) + ".";
            }

            return $"weather: following, current {current}, target " +
                (_hasTarget ? string.Join("/", Target.Select(value => value.ToString("0.00"))) : "none yet") +
                $", own updater {(_frozen ? "frozen" : "NOT frozen")}.";
        }

        private static bool TryGetUpdater(out WeatherStateUpdater updater)
        {
            updater = null!;

            try
            {
                if (!WeatherGameHandler.TryGet(out WeatherGameHandler handler) || handler == null) return false;

                WeatherStateUpdater found = handler.StateUpdater;
                if (found == null || !found.HasInit()) return false;

                updater = found;
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Weather] reaching the weather system failed: " + e);
                return false;
            }
        }
    }
}
