using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova.Weather;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Puts every machine under the host's sky.
    ///
    /// Weather is one of the world's dice rolls: each machine's updater rerolls every in-game hour, so
    /// two worlds drift apart within minutes and no amount of variable syncing brings them back - the
    /// weather flags other systems read are outputs of the roll, not inputs to it. So the host sends
    /// its five weather parameters, and a client freezes its own updater and glides toward them
    /// through the game's own apply path.
    /// </summary>
    internal static class WeatherSync
    {
        /// <summary>How often the host reports, when something moved.</summary>
        private const float SendSeconds = 2f;

        /// <summary>How often the host reports regardless, so a drop is healed.</summary>
        private const float KeepaliveSeconds = 15f;

        /// <summary>Below this, a parameter has not moved.</summary>
        private const float Epsilon = 0.001f;

        /// <summary>
        /// How long a client takes to glide onto reported values. The host lerps its weather over
        /// minutes, so a follower stepping once per report would visibly jump; gliding across the
        /// report interval keeps the sky continuous.
        /// </summary>
        private const float FollowSeconds = 3f;

        private static readonly float[] _lastSent = new float[WeatherMessage.ParamCount];
        private static readonly float[] _target = new float[WeatherMessage.ParamCount];

        private static bool _hasTarget;
        private static bool _frozen;
        private static float _sinceSend;
        private static float _sinceForced;

        /// <summary>
        /// Lets go of the sky. Called when a session ends, so the local updater decides again.
        /// </summary>
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
                    Follow(unscaledDeltaTime);
                    return;

                default:
                    Reset();
                    return;
            }
        }

        /// <summary>
        /// Reads the current parameters, for the report a joining player gets with the handshake.
        /// </summary>
        /// <param name="message">The report.</param>
        /// <returns>False when the weather system is not up.</returns>
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
                if (System.Math.Abs(message.Params[index] - _lastSent[index]) > Epsilon)
                {
                    moved = true;
                    break;
                }
            }

            if (!moved && _sinceForced < KeepaliveSeconds) return;

            _sinceForced = 0f;
            message.Params.CopyTo(_lastSent, 0);
            CoopSession.BroadcastWeather(message);
        }

        private static void Follow(float unscaledDeltaTime)
        {
            while (CoopSession.TryTakeWeather(out WeatherMessage message))
            {
                if (message.Params == null) continue;

                message.Params.CopyTo(_target, 0);
                _hasTarget = true;
            }

            if (!_hasTarget) return;

            if (!TryGetUpdater(out WeatherStateUpdater updater)) return;

            if (!_frozen)
            {
                _frozen = true;
                updater._isEnabled = false;
            }

            // The game's own transition has to be cancelled, not merely frozen, and this is the whole
            // reason the host's fog never reached the client. SetWeatherParamLevel writes into the
            // interpolator's TARGET state while a lerp is running and only into CurState when one is
            // not - but the follow below READS CurState. Reads and writes then address different
            // objects, so the sky never moves, the mod recomputes the same step forever, and because
            // the updater is frozen the lerp can never finish to release it. Measured live: a client
            // sat at 1.00/0/0/0/0 against a target of 0/0/0/0/2 for half a minute.
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
                float next = current + (_target[index] - current) * step;

                if (System.Math.Abs(next - current) < Epsilon && System.Math.Abs(_target[index] - current) < Epsilon)
                {
                    continue;
                }

                // The game's own knob: writes the parameter and applies it to the effect mapper.
                updater.SetWeatherParamLevel((WeatherParam)index, next);
            }
        }

        /// <summary>
        /// One line saying what this machine's half of the sync is doing. For <c>coop_status</c>,
        /// because "the rain did not cross" needs to say which link broke.
        /// </summary>
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
                    string.Join("/", _lastSent.Select(value => value.ToString("0.00"))) + ".";
            }

            return $"weather: following, current {current}, target " +
                (_hasTarget ? string.Join("/", _target.Select(value => value.ToString("0.00"))) : "none yet") +
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
            catch (System.Exception e)
            {
                MelonLogger.Error("[Weather] reaching the weather system failed: " + e);
                return false;
            }
        }
    }
}
