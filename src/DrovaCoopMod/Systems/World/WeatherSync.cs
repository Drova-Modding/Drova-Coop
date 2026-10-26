using System.Diagnostics.CodeAnalysis;
using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova.Weather;
using MelonLoader;

namespace DrovaCoopMod.Systems.World
{
    /// <summary>
    /// Puts every machine under the host's sky. The host sends its weather parameters, and a client
    /// freezes its own updater, which rerolls hourly, and glides toward them.
    /// </summary>
    internal sealed class WeatherSync : ICoopSystem
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
        private static float _lastLightning;

        private static readonly Queue<WeatherMessage> IncomingWeather = new();

        /// <summary>Strikes the host announced. For tests.</summary>
        internal static int ThunderSent { get; private set; }

        /// <summary>Strikes a client played for the host. For tests.</summary>
        internal static int ThunderShown { get; private set; }

        /// <summary>Lets go of the sky. Called when a session ends.</summary>
        private static void Reset()
        {
            if (_frozen)
            {
                _frozen = false;

                if (TryGetUpdater(out WeatherStateUpdater? updater))
                {
                    updater._isEnabled = true;
                }
            }

            _hasTarget = false;
            _sinceSend = 0f;
            _sinceForced = 0f;
        }

        /// <summary>Reports or follows, whichever this machine is.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (CoopSession.ArenaMode) return;

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

            if (!TryGetUpdater(out WeatherStateUpdater? updater)) return false;

            Il2Cpp.WeatherState? state = updater.CurState;
            if (state == null) return false;

            float[] values = new float[WeatherMessage.ParamCount];

            for (int index = 0; index < values.Length; index++)
            {
                values[index] = state.GetParamValue(index);
            }

            message = new WeatherMessage { Params = values };
            return true;
        }

        /// <summary>The storm's thunder effect, whose strikes each machine rolls on its own cooldown.</summary>
        private static WeatherEffect_Thunder? Thunder()
        {
            if (!WeatherGameHandler.TryGet(out WeatherGameHandler handler) || handler == null || handler._currentEffects == null) return null;

            Il2CppSystem.Collections.Generic.List<AWeatherEffect> effects = handler._currentEffects;

            for (int index = 0; index < effects.Count; index++)
            {
                AWeatherEffect? effect = effects[index];
                if (effect == null) continue;

                WeatherEffect_Thunder? thunder = effect.TryCast<WeatherEffect_Thunder>();
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
                BroadcastThunder();
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
        private static void TakeThunder()
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
            BroadcastWeather(message);
        }

        private static void Follow(float unscaledDeltaTime)
        {
            while (TryTakeWeather(out WeatherMessage message))
            {
                if (message.Params == null) continue;

                message.Params.CopyTo(Target, 0);
                _hasTarget = true;
            }

            if (!_hasTarget) return;

            if (!TryGetUpdater(out WeatherStateUpdater? updater)) return;

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

            Il2Cpp.WeatherState? state = updater.CurState;
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
            if (!TryGetUpdater(out WeatherStateUpdater? updater))
            {
                return "weather: the weather system is not reachable here.";
            }

            Il2Cpp.WeatherState? state = updater.CurState;
            string current = state == null
                ? "?"
                : string.Join("/", Enumerable.Range(0, WeatherMessage.ParamCount)
                    .Select(index => state.GetParamValue(index).ToString("0.00")));

            if (CoopSession.HostsWorld)
            {
                return $"weather: reporting, current {current}, last sent " +
                    string.Join("/", LastSent.Select(value => value.ToString("0.00"))) + ".";
            }

            return $"weather: following, current {current}, target " +
                (_hasTarget ? string.Join("/", Target.Select(value => value.ToString("0.00"))) : "none yet") +
                $", own updater {(_frozen ? "frozen" : "NOT frozen")}.";
        }

        private static bool TryGetUpdater([NotNullWhen(true)] out WeatherStateUpdater? updater)
        {
            updater = null;

            try
            {
                if (!WeatherGameHandler.TryGet(out WeatherGameHandler handler) || handler == null) return false;

                WeatherStateUpdater? found = handler.StateUpdater;
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

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<WeatherMessage>(MessageIds.Weather, OnWeather);
            CoopSession.Register<ThunderMessage>(MessageIds.Thunder, (_, _) =>
            {
                if (CoopSession.IsFromHost()) TakeThunder();
            });
        }

        void ICoopSystem.EndSession()
        {
            IncomingWeather.Clear();
        }

        /// <summary>Takes the next weather report from the host.</summary>
        private static bool TryTakeWeather(out WeatherMessage message)
            => IncomingWeather.TryDequeue(out message);

        /// <summary>Tells every client the host's weather. Host only, since everybody follows the host's dice.</summary>
        private static void BroadcastWeather(in WeatherMessage message)
            => CoopSession.SendIfHost(message);

        private static void OnWeather(INetPeer peer, WeatherMessage message)
        {
            if (!CoopSession.IsFromHost()) return;

            // Superseded by the next report, so drop the oldest.
            CoopSession.EnqueueLatest(IncomingWeather, message, 4);
        }

        /// <summary>A thunder strike in the host's storm: to everyone.</summary>
        private static void BroadcastThunder()
        {
            if (CoopSession.HostsWorld) NetworkAccess.SendToAll(new ThunderMessage());
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (!loaded) Reset();
        }
    }
}
