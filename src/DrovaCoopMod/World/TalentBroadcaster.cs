using Drova_Modding_API.Access;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.Talent;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Sends the local player's learned talents when they change, and to anybody who arrives.
    ///
    /// Talents decide how a body fights, not only how hard: whether a combo continues, how a weapon's swings are
    /// timed and animated, whether a guard has a perfect window. The game asks the body swinging, so a teammate's
    /// copy - cloned once, never told what its owner learned since - swung differently from its owner.
    /// </summary>
    internal static class TalentBroadcaster
    {
        private const float CheckInterval = 1f;

        private static string[] _lastSent = [];
        private static float _sinceLastCheck;
        private static int _lastRosterCount;

        internal static void Update(float unscaledDeltaTime)
        {
            if (!CoopSession.IsLive)
            {
                Reset();
                return;
            }

            _sinceLastCheck += unscaledDeltaTime;
            if (_sinceLastCheck < CheckInterval) return;
            _sinceLastCheck = 0f;

            if (!PlayerAccess.TryGetPlayer(out Actor player)) return;

            try
            {
                TalentActorModule? talents = player.TalentActorModule?.TryCast<TalentActorModule>();
                if (talents == null || talents._learnedTalents == null) return;

                List<string> current = new();
                foreach (string guid in talents._learnedTalents) current.Add(guid);
                current.Sort(StringComparer.Ordinal);

                // Somebody arriving never heard what this player learned before they came.
                bool rosterChanged = CoopSession.Players.Count != _lastRosterCount;
                _lastRosterCount = CoopSession.Players.Count;
                if (!rosterChanged && current.SequenceEqual(_lastSent)) return;

                _lastSent = current.ToArray();
                CoopSession.BroadcastTalents(_lastSent);
                MelonLogger.Msg($"[Talent] sent {_lastSent.Length} learned talents.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Talent] reading this player's talents failed: " + e);
            }
        }

        internal static void Reset()
        {
            _lastSent = [];
            _sinceLastCheck = 0f;
            _lastRosterCount = 0;
        }
    }
}
