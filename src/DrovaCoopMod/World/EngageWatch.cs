using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Tells the session when this player is personally engaged with a session NPC. A client reports
    /// dialogues and brawls so the host stands its copies down - a brawl outright leases the creature
    /// to this machine. A host announces its own scripted brawls so a teammate can join them.
    ///
    /// Both scenes are staged by graphs that run only on the machine whose player stands in them. The
    /// dialogue runs here against a body whose brain is on the host, so without a word the host walks
    /// the conversation partner away mid-sentence; the brawl runs here too, so the host's copy of the
    /// opponent only ever sees an attacker and fights like it means it - which is how a provoked
    /// mining chef became an NPC that beat a downed player forever.
    ///
    /// Watched by polling actor state rather than by patching the brawl or dialogue systems, because
    /// both engagements already leave their mark on the actors: a brawl sets <c>CustomStats.IsInBrawl</c>
    /// on every participant, on both copies, and a dialogue engages the partner's own dialogue module.
    /// A poll cannot be inlined away, and the mark going out is the engagement ending however it ended -
    /// knockout, run-away, abort - with no per-cause plumbing.
    /// </summary>
    internal static class EngageWatch
    {
        private const float SweepSeconds = 0.5f;

        /// <summary>
        /// How often a live engagement is repeated. The host forgets one it has not heard about for
        /// three of these, so a client that dies mid-brawl cannot leave an NPC held forever.
        /// </summary>
        private const float KeepaliveSeconds = 5f;

        /// <summary>
        /// How long a brawl lease outlives the brawl. A scripted brawl's epilogue - the world
        /// dialogue that captures the loser, ports and heals - starts the frame the brawl resolves,
        /// on the winner's body. Dropping the lease at that moment hands the body back to the host,
        /// which resumes driving it within a snapshot and smothers the epilogue; the mine's bandits
        /// beat this player and then forgot to capture them, every time. While a lingering body is
        /// in a dialogue the linger refreshes, so an epilogue longer than the window still finishes.
        /// </summary>
        private const float BrawlLingerSeconds = 12f;

        private static readonly Dictionary<EnemyNetId, byte> _sent = new();
        private static readonly Dictionary<EnemyNetId, float> _sinceSent = new();
        private static readonly Dictionary<EnemyNetId, float> _lingering = new();
        private static readonly List<EnemyNetId> _toDrop = new();

        private static float _sinceSweep;
        private static int _reported;
        private static bool _lingerReported;

        /// <summary>How many engagement transitions were reported. For <c>coop_status</c>.</summary>
        internal static int Reported => _reported;

        /// <summary>Whether any engagement is live right now. For <c>coop_status</c>.</summary>
        internal static bool Engaged => _sent.Count > 0;

        /// <summary>
        /// Whether this player's scripted brawl against this creature is live. On a client this is the
        /// lease: the registry bares the body so the local fight is real, and the damage watch keeps
        /// its blows local.
        /// </summary>
        internal static bool IsBrawling(EnemyNetId id)
        {
            return _sent.TryGetValue(id, out byte mode) && mode == EnemyEngageMessage.ModeBrawl;
        }

        /// <summary>Forgets everything. Called when a session ends.</summary>
        internal static void Reset()
        {
            _sent.Clear();
            _sinceSent.Clear();
            _lingering.Clear();
            _sinceSweep = 0f;
            _reported = 0;
            _lingerReported = false;
        }

        /// <summary>
        /// Watches the local player's engagements and keeps the host informed. Call once per frame from
        /// a loaded world.
        /// </summary>
        /// <param name="unscaledDeltaTime">Seconds since the last frame.</param>
        internal static void Update(float unscaledDeltaTime)
        {
            // A client reports its engagements so the host stands its copies down; a host announces its
            // scripted brawls so a teammate can join them. The host's dialogues stay its own business -
            // they run against actors whose brains are right there.
            bool client = CoopSession.State == CoopSessionState.Joined;
            bool host = CoopSession.State == CoopSessionState.Hosting;

            if (!client && !host)
            {
                if (_sent.Count > 0) Reset();
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
            if (!PlayerAccess.TryGetPlayer(out Actor player) || player == null) return;

            bool inBrawl = player.CustomStats != null && player.CustomStats.IsInBrawl;
            bool inDialogue = !host && player.DialogueModule != null && player.DialogueModule.IsInDialogue();

            _toDrop.Clear();
            _toDrop.AddRange(_sent.Keys);

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

                    Consider(bound.Key, bound.Value, inBrawl, inDialogue, elapsed, host: false);
                }
            }

            // Engagements that ended, and engagements whose body went away - released either way,
            // because a held NPC with nobody engaging it is exactly the strand the keepalive exists
            // to prevent.
            foreach (EnemyNetId id in _toDrop)
            {
                _sent.Remove(id);
                _sinceSent.Remove(id);
                _lingering.Remove(id);
                _reported++;

                Send(id, EnemyEngageMessage.ModeNone, host);
                MelonLogger.Msg(host
                    ? "[Engage] the host's scripted brawl ended; the teammates were told."
                    : "[Engage] an engagement with one of the host's NPCs ended; the host was told.");
            }
        }

        private static void Consider(EnemyNetId id, Actor body, bool inBrawl, bool inDialogue,
            float elapsed, bool host)
        {
            byte mode = ModeOf(body, inBrawl, inDialogue);

            if (!host)
            {
                mode = Linger(id, body, mode, elapsed);
            }

            if (mode == EnemyEngageMessage.ModeNone) return;

            _toDrop.Remove(id);

            bool known = _sent.TryGetValue(id, out byte sentMode);
            float since = _sinceSent.TryGetValue(id, out float value) ? value + elapsed : 0f;

            if (known && sentMode == mode && since < KeepaliveSeconds)
            {
                _sinceSent[id] = since;
                return;
            }

            Send(id, mode, host);
            _sinceSent[id] = 0f;

            if (!known || sentMode != mode)
            {
                _sent[id] = mode;
                _reported++;

                MelonLogger.Msg(host
                    ? "[Engage] the host is in a scripted brawl; the teammates were invited to help."
                    : mode == EnemyEngageMessage.ModeBrawl
                        ? "[Engage] this player is in a scripted brawl with one of the host's NPCs; the host was told."
                        : "[Engage] this player is talking to one of the host's NPCs; the host was asked to hold it still.");
            }
        }

        /// <summary>
        /// Keeps a brawl lease alive through the brawl's epilogue.
        ///
        /// A live brawl arms the linger; when the brawl resolves, the lease is reported onward for
        /// the linger window instead of dropping, and a dialogue on the body - the capture playing
        /// out - refreshes the window until it ends. Only then does the lease really drop and the
        /// host take its creature back.
        /// </summary>
        private static byte Linger(EnemyNetId id, Actor body, byte mode, float elapsed)
        {
            if (mode == EnemyEngageMessage.ModeBrawl)
            {
                _lingering[id] = BrawlLingerSeconds;
                return mode;
            }

            if (!_lingering.TryGetValue(id, out float remaining)) return mode;

            if (mode != EnemyEngageMessage.ModeNone)
            {
                _lingering.Remove(id);
                return mode;
            }

            bool inEpilogue = body.DialogueModule != null && body.DialogueModule.IsInDialogue();
            remaining = inEpilogue ? BrawlLingerSeconds : remaining - elapsed;

            if (remaining <= 0f)
            {
                _lingering.Remove(id);
                return EnemyEngageMessage.ModeNone;
            }

            _lingering[id] = remaining;

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

        /// <summary>
        /// What this player is doing with this body right now.
        ///
        /// The brawl takes precedence: a brawl started from a dialogue leaves the dialogue closing while
        /// the fight begins, and it is the fight the host has to know about.
        /// </summary>
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
