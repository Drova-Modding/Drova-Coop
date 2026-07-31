#if DEBUG
using Drova_Modding_API.Access;
using DrovaCoopMod.World;
using Il2CppDrova;
using MelonLoader;

namespace DrovaCoopMod.Diagnostics
{
    /// <summary>
    /// Answers the one question the existing probes cannot: does the health funnel fire for things that
    /// are not players?
    ///
    /// <see cref="PatchProbe"/> counts invocations of a patched method, which proves the patch runs. It
    /// cannot prove the patch runs *for a wolf*, and everything about replicating the world rests on that
    /// - if <c>Health.CheckEvents</c> turned out to be reached only through the paths a player's damage
    /// takes, every creature would die unobserved and the first slice would appear to work in testing
    /// while doing nothing in a fight.
    ///
    /// It also counts what cannot be named. A creature spawned at runtime carries no authored guid, so it
    /// can never be reported; the share of the world in that state is the honest ceiling on how much of
    /// this design can ever work, and it is worth a number rather than a guess.
    /// </summary>
    internal static class EnemyProbe
    {
        private static int _players;
        private static int _puppets;
        private static int _others;

        /// <summary>
        /// Records which sort of body this health change belonged to. Called from the funnel postfix
        /// before every other guard, so the count is of everything that reaches it.
        /// </summary>
        internal static void Count(Health health)
        {
            try
            {
                Actor? actor = health?.OwnerEntity?.TryCast<Actor>();
                if (actor == null) return;

                if (RemotePlayers.IsPuppet(actor))
                {
                    _puppets++;
                    return;
                }

                if (PlayerAccess.TryGetPlayer(out Actor localPlayer)
                    && localPlayer != null
                    && localPlayer.Pointer == actor.Pointer)
                {
                    _players++;
                    return;
                }

                _others++;
            }
            catch
            {
                // Deliberately silent and deliberately empty. This is a counter on the path of every point
                // of damage in the game; a probe that logs from there is worse than a probe that misses a
                // count.
            }
        }

        /// <summary>
        /// Prints what has been seen, and what share of the world could be named on the wire if it died.
        /// </summary>
        internal static void Report()
        {
            MelonLogger.Msg("[Probe] ---- health funnel, by owner ----");
            MelonLogger.Msg($"[Probe] local player {_players}, remote bodies {_puppets}, everything else {_others}");

            if (_others == 0)
            {
                MelonLogger.Warning("[Probe] nothing but players has been through the funnel. Either nothing " +
                    "else has been damaged yet, or the funnel is not the funnel for creatures - hit a wolf " +
                    "and look again before trusting enemy death replication.");
            }

            ReportNameable();
        }

        /// <summary>
        /// Counts how many live actors carry an authored guid, which is the only thing this mod can name a
        /// creature by.
        ///
        /// Read straight off <c>EntityGameHandler</c>'s own dictionary rather than by sweeping the scene:
        /// it is the index the game itself resolves guids through, so what it holds is exactly what a
        /// reported death will be able to find.
        /// </summary>
        private static void ReportNameable()
        {
            try
            {
                if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null)
                {
                    MelonLogger.Msg("[Probe] no entity handler; load a world first.");
                    return;
                }

                Il2CppSystem.Collections.Generic.Dictionary<string, Actor> byGuid = entities._entityGuidDictionary;
                if (byGuid == null)
                {
                    MelonLogger.Warning("[Probe] the entity guid dictionary is not reachable on the proxy. " +
                        "Enemy identity would have to come from a scene sweep instead.");
                    return;
                }

                int authored = 0;
                int malformed = 0;

                foreach (Il2CppSystem.Collections.Generic.KeyValuePair<string, Actor> pair in byGuid)
                {
                    if (pair.Key != null && pair.Key.Length == Messages.EnemyDeathMessage.MaxGuidLength)
                    {
                        authored++;
                        continue;
                    }

                    malformed++;
                }

                MelonLogger.Msg($"[Probe] {authored} registered actors carry an authored guid, {malformed} do not.");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[Probe] could not read the entity guid dictionary: " + e.Message);
            }
        }
    }
}
#endif
