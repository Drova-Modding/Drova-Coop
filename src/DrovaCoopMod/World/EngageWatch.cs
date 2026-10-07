using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Reports this player's engagements with session NPCs. A client's dialogue makes the host hold its copy still,
    /// and a client's brawl leases the creature to this machine. A host announces its scripted brawls so teammates can join.
    /// Polled from actor state (<c>CustomStats.IsInBrawl</c>, the dialogue module), so any way of ending counts.
    /// </summary>
    internal static class EngageWatch
    {
        private const float SweepSeconds = 0.5f;

        /// <summary>The host forgets an engagement after three missed keepalives.</summary>
        private const float KeepaliveSeconds = 5f;

        /// <summary>
        /// How long a brawl lease outlives the brawl, so the epilogue dialogue (capture, port, heal) runs here.
        /// A dialogue on the body refreshes it.
        /// </summary>
        private const float BrawlLingerSeconds = 12f;

        /// <summary>
        /// Caps how much linger one frame can spend, so a long hitch does not drop the lease.
        /// Not applied to the keepalive clock, which must see the full stall.
        /// </summary>
        private const float MaxLingerStepSeconds = 1f;

        private static readonly Dictionary<EnemyNetId, byte> Sent = new();
        private static readonly Dictionary<EnemyNetId, float> SinceSent = new();
        private static readonly Dictionary<EnemyNetId, float> Lingering = new();
        private static readonly List<EnemyNetId> ToDrop = new();

        private static float _sinceSweep;
        private static bool _lingerReported;

        /// <summary>How many engagement transitions were reported. For <c>coop_status</c>.</summary>
        internal static int Reported { get; private set; }

        /// <summary>Whether this player's scripted brawl against this creature is live. On a client this is the lease.</summary>
        internal static bool IsBrawling(EnemyNetId id)
        {
            return Sent.TryGetValue(id, out byte mode) && mode == EnemyEngageMessage.ModeBrawl;
        }

        /// <summary>
        /// Whether this player is in a dialogue or brawl with this creature. The snapshot applier skips such bodies,
        /// since host positions would stop a dialogue cutscene's walks from ever arriving.
        /// </summary>
        internal static bool IsEngaged(EnemyNetId id)
        {
            return Sent.ContainsKey(id);
        }

        /// <summary>Forgets everything. Called when a session ends.</summary>
        internal static void Reset()
        {
            Sent.Clear();
            SinceSent.Clear();
            Lingering.Clear();
            _sinceSweep = 0f;
            Reported = 0;
            _lingerReported = false;
        }

        /// <summary>Watches the local player's engagements. Call once per frame from a loaded world.</summary>
        internal static void Update(float unscaledDeltaTime)
        {
            // The host's dialogues are not reported, since its NPCs' brains are local.
            bool client = CoopSession.State == CoopSessionState.Joined;
            bool host = CoopSession.State == CoopSessionState.Hosting;

            if (!client && !host)
            {
                if (Sent.Count > 0) Reset();
                return;
            }

            _sinceSweep += unscaledDeltaTime;
            if (_sinceSweep < SweepSeconds) return;

            float elapsed = _sinceSweep;
            _sinceSweep = 0f;

            try
            {
                Sweep(elapsed, host);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Engage] watching this player's engagements failed: " + e);
            }
        }

        private static void Sweep(float elapsed, bool host)
        {
            if (!PlayerAccess.TryGetPlayer(out Actor player)) return;

            bool inBrawl = player.CustomStats != null && player.CustomStats.IsInBrawl;
            bool inDialogue = !host && player.DialogueModule != null && player.DialogueModule.IsInDialogue();
            bool arenaEvent = !host && ArenaGates.RunsTheFight();

            ToDrop.Clear();
            ToDrop.AddRange(Sent.Keys);

            if (host)
            {
                foreach (EnemyNetId id in EnemyRegistry.ClaimedIds)
                {
                    if (!EnemyRegistry.TryResolveOnHost(id, out Actor claimed) || claimed == null) continue;

                    Consider(id, claimed, inBrawl, inDialogue: false, elapsed, host: true);
                }
            }
            else
            {
                foreach (KeyValuePair<EnemyNetId, Actor> bound in EnemyRegistry.BoundBodies)
                {
                    if (bound.Value == null) continue;

                    Consider(bound.Key, bound.Value, inBrawl, inDialogue, elapsed, host: false,
                        arenaEvent && ArenaGates.AtTheArena(bound.Value.GetFeetPosition()));
                }
            }

            // Ended engagements, and those whose body went away.
            foreach (EnemyNetId id in ToDrop)
            {
                bool wasBrawl = Sent.TryGetValue(id, out byte endedMode)
                    && endedMode == EnemyEngageMessage.ModeBrawl;

                Sent.Remove(id);
                SinceSent.Remove(id);
                Lingering.Remove(id);
                Reported++;

                Send(id, EnemyEngageMessage.ModeNone, host);
                MelonLogger.Msg(host
                    ? "[Engage] the host's scripted brawl ended; the teammates were told."
                    : "[Engage] an engagement with one of the host's NPCs ended; the host was told.");

                if (wasBrawl)
                {
                    StandDown(id, host);
                }
            }
        }

        /// <summary>
        /// After a brawl, drops the hostility relayed blows gave the creature toward teammates.
        /// The brawl only reverts alignments it installed itself, and an alignment otherwise lasts forever.
        /// </summary>
        private static void StandDown(EnemyNetId id, bool host)
        {
            try
            {
                Actor? body = null;

                if (host)
                {
                    if (EnemyRegistry.TryResolveOnHost(id, out Actor claimed)) body = claimed;
                }
                else
                {
                    EnemyRegistry.BoundBodies.TryGetValue(id, out body);
                }

                if (body == null) return;

                EnemyThreat.PardonAll(body);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Engage] standing a creature down after its brawl failed: " + e);
            }
        }

        private static void Consider(EnemyNetId id, Actor body, bool inBrawl, bool inDialogue,
            float elapsed, bool host, bool atOwnArena = false)
        {
            // Brawls do not cross, so a client copy in one is leased even when the player only watches (Jero vs Molvina).
            // A knocked-out body also reads as in a brawl, but stays the host's.
            byte mode = ModeOf(body, inBrawl || (!host && body.CustomStats != null && !body.CustomStats.IsKnockout), inDialogue);

            // NPCs staged by this client's own arena event, and its fight's summons, are leased like a brawl.
            if (atOwnArena || (!host && LeaseSummons.IsOwn(id) && body.GetHealth() is { IsDead: false })) mode = EnemyEngageMessage.ModeBrawl;

            if (!host)
            {
                mode = Linger(id, body, mode, Math.Min(elapsed, MaxLingerStepSeconds));
            }

            if (mode == EnemyEngageMessage.ModeNone) return;

            ToDrop.Remove(id);

            bool known = Sent.TryGetValue(id, out byte sentMode);
            float since = SinceSent.TryGetValue(id, out float value) ? value + elapsed : 0f;

            if (known && sentMode == mode && since < KeepaliveSeconds)
            {
                SinceSent[id] = since;
                return;
            }

            Send(id, mode, host);
            SinceSent[id] = 0f;

            if (!known || sentMode != mode)
            {
                Sent[id] = mode;
                Reported++;

                MelonLogger.Msg(host
                    ? "[Engage] the host is in a scripted brawl; the teammates were invited to help."
                    : mode == EnemyEngageMessage.ModeBrawl
                        ? "[Engage] this player is in a scripted brawl with one of the host's NPCs; the host was told."
                        : "[Engage] this player is talking to one of the host's NPCs; the host was asked to hold it still.");
            }
        }

        /// <summary>Keeps a brawl lease alive through the brawl's epilogue.</summary>
        private static byte Linger(EnemyNetId id, Actor body, byte mode, float elapsed)
        {
            if (mode == EnemyEngageMessage.ModeBrawl)
            {
                Lingering[id] = BrawlLingerSeconds;
                return mode;
            }

            if (!Lingering.TryGetValue(id, out float remaining)) return mode;

            if (mode != EnemyEngageMessage.ModeNone)
            {
                Lingering.Remove(id);
                return mode;
            }

            bool inEpilogue = body.DialogueModule != null && body.DialogueModule.IsInDialogue();
            remaining = inEpilogue ? BrawlLingerSeconds : remaining - elapsed;

            if (remaining <= 0f)
            {
                Lingering.Remove(id);
                return EnemyEngageMessage.ModeNone;
            }

            Lingering[id] = remaining;

            if (!_lingerReported)
            {
                _lingerReported = true;
                MelonLogger.Msg("[Engage] a scripted brawl resolved; its lease is kept while the " +
                    "epilogue plays, so the story that follows a lost fight can happen here.");
            }

            return EnemyEngageMessage.ModeBrawl;
        }

        private static void Send(EnemyNetId id, byte mode, bool host)
        {
            if (host)
            {
                CoopSession.AnnounceEnemyEngage(id, mode);
            }
            else
            {
                CoopSession.ReportEnemyEngage(id, mode);
            }
        }

        /// <summary>A brawl wins over a dialogue, since a brawl started from a dialogue overlaps its closing.</summary>
        private static byte ModeOf(Actor body, bool playerInBrawl, bool playerInDialogue)
        {
            if (playerInBrawl && body.CustomStats != null && body.CustomStats.IsInBrawl)
            {
                return EnemyEngageMessage.ModeBrawl;
            }

            if (playerInDialogue && body.DialogueModule != null && body.DialogueModule.IsInDialogue())
            {
                return EnemyEngageMessage.ModeDialogue;
            }

            return EnemyEngageMessage.ModeNone;
        }
    }
}
