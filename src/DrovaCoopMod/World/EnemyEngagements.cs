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

        private static int _held;
        private static int _leases;

        /// <summary>How many dialogues held an NPC still. For <c>coop_status</c>.</summary>
        internal static int Held => _held;

        /// <summary>How many brawls were leased to the client fighting them. For <c>coop_status</c>.</summary>
        internal static int Leases => _leases;

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
            _held = 0;
            _leases = 0;
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

            _expired.Clear();

            foreach (KeyValuePair<EnemyNetId, Engagement> entry in _engagements)
            {
                entry.Value.SinceHeard += unscaledDeltaTime;

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
            engagement.Held = true;

            if (engagement.Mode == EnemyEngageMessage.ModeDialogue)
            {
                _held++;
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
            _leases++;
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
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Engage] releasing an NPC from an engagement failed: " + e);
            }
        }
    }
}
