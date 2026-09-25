using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// The host's side of a client's engagement with one of its NPCs.
    ///
    /// Both engagements stand the host's copy down, because the scene runs on the client. A dialogue
    /// holds the NPC still: its AI is stopped and its snapshots are withheld, so the client's
    /// conversation is not fought by a brain that cannot see it. A brawl goes further and leases the
    /// creature to the client outright: the host stops driving it, so the client's own copy - which the
    /// brawl was installed on - fights for real over there, resolves a winner, and runs the quest
    /// wiring hanging off the result. Mirroring knockouts instead was tried and lost the mine capture:
    /// a brawl whose opponents are driven bodies collapses before anyone can lose it.
    ///
    /// The host's held copy is made invincible while leased, so a host player joining the fight cannot
    /// lethally wound a quest NPC through the copy that is not playing. The blows still compute and
    /// still fire events, which is what lets <see cref="DamageWatch"/> relay them into the real fight.
    ///
    /// Every engagement expires if not repeated. A client that crashes mid-dialogue must not leave an
    /// NPC standing frozen in a field for the rest of the session.
    /// </summary>
    internal static class EnemyEngagements
    {
        /// <summary>
        /// Three missed keepalives. The client repeats every five seconds, so a healthy engagement is
        /// never near this.
        /// </summary>
        private const float ExpirySeconds = 15f;

        /// <summary>
        /// The longest frame that is allowed to age an engagement.
        ///
        /// A frame is the only clock this has, and a frame is not always a frame. On 2026-08-09 the
        /// host went at least fifteen seconds between two of them: a backlog of a client's engage
        /// messages drained in one pass, four NPCs were held - and the very next statement added those
        /// same seconds to every clock and expired all four, three milliseconds later, while the client
        /// was still mid-dialogue for another two seconds.
        ///
        /// Nothing was observed during a frame like that, so nothing should be aged by it. Keepalives
        /// that arrived meanwhile have already been drained above and have said the engagement is
        /// alive; a client that really did go away is expired by the frames that follow, at their real
        /// length.
        /// </summary>
        private const float MaxAgeStepSeconds = 1f;

        private sealed class Engagement
        {
            public PlayerId Player;
            public byte Mode;
            public float SinceHeard;
            public bool Held;
            public bool Shielded;
        }

        private static readonly Dictionary<EnemyNetId, Engagement> _engagements = new();
        private static readonly List<EnemyNetId> _expired = new();

        /// <summary>
        /// Frames too long to age a teammate's engagement with. For <c>coop_status</c>.
        ///
        /// This counts only frames that had a live engagement to protect, because it sits below the
        /// guard that returns when there are none. The honest figure for how often this machine stalls
        /// at all is the <c>frames:</c> line, which counts them in every mode.
        /// </summary>
        internal static int StallsIgnored { get; private set; }

        /// <summary>How many dialogues held an NPC still. For <c>coop_status</c>.</summary>
        internal static int Held { get; private set; }

        /// <summary>How many brawls were leased to the client fighting them. For <c>coop_status</c>.</summary>
        internal static int Leases { get; private set; }

        /// <summary>Whether a client is in a scripted brawl right now, lingering epilogue included.</summary>
        internal static bool AnyLeasedBrawl
        {
            get
            {
                foreach (Engagement engagement in _engagements.Values)
                {
                    if (engagement.Mode == EnemyEngageMessage.ModeBrawl) return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Whether this NPC is engaged by a client, in which case its snapshots are withheld: a
        /// dialogue animates the body over there, and a leased brawl moves it over there.
        /// </summary>
        internal static bool IsHeld(EnemyNetId id)
        {
            return _engagements.ContainsKey(id);
        }

        /// <summary>
        /// Whether this NPC's brawl is leased to a client, and to whom. While it is, the host must not
        /// drive the creature, and the host player's blows on it belong to that client's fight.
        /// </summary>
        internal static bool TryGetLease(EnemyNetId id, out PlayerId player)
        {
            if (_engagements.TryGetValue(id, out Engagement? engagement)
                && engagement.Mode == EnemyEngageMessage.ModeBrawl)
            {
                player = engagement.Player;
                return true;
            }

            player = PlayerId.None;
            return false;
        }

        /// <summary>Releases everything. Called when a session ends.</summary>
        internal static void Reset()
        {
            foreach (KeyValuePair<EnemyNetId, Engagement> entry in _engagements)
            {
                End(entry.Key, entry.Value);
            }

            _engagements.Clear();
            Held = 0;
            Leases = 0;
            StallsIgnored = 0;
        }

        /// <summary>
        /// Applies what clients report and expires what they stopped reporting. Call once per frame from
        /// a loaded world.
        /// </summary>
        /// <param name="unscaledDeltaTime">Seconds since the last frame.</param>
        internal static void Update(float unscaledDeltaTime)
        {
            if (CoopSession.State != CoopSessionState.Hosting)
            {
                if (_engagements.Count > 0) Reset();
                return;
            }

            while (CoopSession.TryTakeEnemyEngage(out EnemyEngageMessage message))
            {
                Apply(message);
            }

            if (_engagements.Count == 0) return;

            float step = unscaledDeltaTime;

            if (step > MaxAgeStepSeconds)
            {
                step = MaxAgeStepSeconds;
                StallsIgnored++;
            }

            _expired.Clear();

            foreach (KeyValuePair<EnemyNetId, Engagement> entry in _engagements)
            {
                entry.Value.SinceHeard += step;

                if (entry.Value.SinceHeard >= ExpirySeconds)
                {
                    _expired.Add(entry.Key);
                }
            }

            foreach (EnemyNetId id in _expired)
            {
                MelonLogger.Warning("[Engage] a client's engagement went quiet and was expired; " +
                    "the NPC was released.");

                End(id, _engagements[id]);
                _engagements.Remove(id);
            }
        }

        private static void Apply(in EnemyEngageMessage message)
        {
            try
            {
                if (!message.IsPlausible()) return;

                bool known = _engagements.TryGetValue(message.NetId, out Engagement? engagement);

                if (message.Mode == EnemyEngageMessage.ModeNone)
                {
                    if (!known) return;

                    End(message.NetId, engagement!);
                    _engagements.Remove(message.NetId);
                    return;
                }

                if (known && engagement!.Mode == message.Mode && engagement.Player.Value == message.Player.Value)
                {
                    engagement.SinceHeard = 0f;
                    return;
                }

                // A mode change is an end and a begin, so a dialogue that turned into a brawl lets go
                // of the hold before it leases the fight.
                if (known)
                {
                    End(message.NetId, engagement!);
                }
                else
                {
                    engagement = new Engagement();
                    _engagements[message.NetId] = engagement;
                }

                engagement!.Player = message.Player;
                engagement.Mode = message.Mode;
                engagement.SinceHeard = 0f;
                engagement.Held = false;
                engagement.Shielded = false;

                Begin(message.NetId, engagement);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Engage] applying a client's engagement failed: " + e);
            }
        }

        private static void Begin(EnemyNetId id, Engagement engagement)
        {
            if (!EnemyRegistry.TryResolveOnHost(id, out Actor actor) || actor == null) return;

            actor.AIModule?.StopAI();

            // StopAI stops the behaviour graph and nothing else - it does not deactivate the AI, so
            // the movement module goes on servicing a path the actor was already following. A held NPC
            // that was mid-errand therefore keeps walking it for the whole hold, with its snapshots
            // withheld from the client that is talking to it, and snaps somewhere else when the hold
            // ends. Nothing on the StopAI path clears that, so this does; the release re-plans anyway.
            actor.GetMovementModule()?._actorFollowPathMovement?.Reset();

            engagement.Held = true;

            if (engagement.Mode == EnemyEngageMessage.ModeDialogue)
            {
                Held++;
                MelonLogger.Msg("[Engage] an NPC was held still while a teammate talks to it.");
                return;
            }

            // Shielded only when the flag is this mod's to give back. A quest may already hold the
            // creature invincible, and that protection is not ours to remove when the brawl ends.
            Health health = actor.GetHealth();

            if (health != null && !health.IsInvincible)
            {
                health.SetInvinciblity(true);
                engagement.Shielded = true;
            }

            // The drive stops through EnemyRegistry reading the lease, and the release reaches the
            // client as an ordinary bind - whose own machinery is what switches the copy's AI back on
            // so the brawl installed on it can fight.
            Leases++;
            MelonLogger.Msg("[Engage] a teammate's scripted brawl leased this NPC; its copy over there " +
                "fights for real and this one stands held.");
        }

        private static void End(EnemyNetId id, Engagement engagement)
        {
            try
            {
                if (!EnemyRegistry.TryResolveOnHost(id, out Actor actor) || actor == null) return;

                if (engagement.Held)
                {
                    actor.AIModule?.StartAI();
                    engagement.Held = false;
                }

                if (engagement.Shielded)
                {
                    actor.GetHealth()?.SetInvinciblity(false);
                    engagement.Shielded = false;
                }

                if (engagement.Mode == EnemyEngageMessage.ModeBrawl)
                {
                    // The fight ran over there and this copy stood held through it - but every blow the
                    // client reported was applied here too, and each one gave their body the standing a
                    // swing in person would have earned. The brawl that owned the ending happened on the
                    // other machine and can take back only its own alignments, so without this the
                    // creature comes out of a settled duel permanently at war with a teammate.
                    EnemyThreat.PardonAll(actor);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Engage] releasing an NPC from an engagement failed: " + e);
            }
        }
    }
}
