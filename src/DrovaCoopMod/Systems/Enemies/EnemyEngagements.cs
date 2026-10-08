using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.Systems.World;
using Il2CppDrova;
using MelonLoader;

namespace DrovaCoopMod.Systems.Enemies
{
    /// <summary>
    /// The host's side of a client's engagement with one of its NPCs. A dialogue holds the NPC still, a brawl
    /// leases it to the client (the held copy invincible). Engagements expire unless repeated.
    /// </summary>
    internal sealed class EnemyEngagements : ICoopSystem
    {
        // Three missed keepalives at the client's five-second repeat.
        private const float ExpirySeconds = 15f;

        // A long stall must not age every engagement at once, since its keepalives were just drained.
        private const float MaxAgeStepSeconds = 1f;

        private static readonly Queue<EnemyEngageMessage> IncomingEnemyEngage = new();
        private static readonly Queue<EnemyEngageMessage> IncomingHostEngage = new();

        private sealed class Engagement
        {
            public PlayerId Player;
            public byte Mode;
            public float SinceHeard;
            public bool HeldForDialogue;
            public bool Shielded;
        }

        private static readonly Dictionary<EnemyNetId, Engagement> Engagements = new();
        private static readonly List<EnemyNetId> Expired = new();

        /// <summary>Over-long frames clamped while an engagement was live. For <c>coop_status</c>.</summary>
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
                foreach (Engagement engagement in Engagements.Values)
                {
                    if (engagement.Mode == EnemyEngageMessage.ModeBrawl) return true;
                }

                return false;
            }
        }

        /// <summary>Whether a client engages this NPC, in which case its snapshots are withheld.</summary>
        internal static bool IsHeld(EnemyNetId id)
        {
            return Engagements.ContainsKey(id);
        }

        /// <summary>Whether this NPC's brawl is leased to a client, and to whom.</summary>
        internal static bool TryGetLease(EnemyNetId id, out PlayerId player)
        {
            if (Engagements.TryGetValue(id, out Engagement engagement)
                && engagement.Mode == EnemyEngageMessage.ModeBrawl)
            {
                player = engagement.Player;
                return true;
            }

            player = PlayerId.None;
            return false;
        }

        /// <summary>Whether this player has a leased brawl running.</summary>
        internal static bool LeasesTo(PlayerId player)
        {
            foreach (Engagement engagement in Engagements.Values)
            {
                if (engagement.Mode == EnemyEngageMessage.ModeBrawl && engagement.Player.Value == player.Value) return true;
            }

            return false;
        }

        /// <summary>Leases a creature to a player as if they had reported its brawl (a summon of their fight).</summary>
        internal static void Lease(EnemyNetId id, PlayerId player)
        {
            Apply(new EnemyEngageMessage { NetId = id, Player = player, Mode = EnemyEngageMessage.ModeBrawl });
        }

        /// <summary>Releases everything. Called when a session ends.</summary>
        internal static void Reset()
        {
            foreach (KeyValuePair<EnemyNetId, Engagement> entry in Engagements)
            {
                End(entry.Key, entry.Value);
            }

            Engagements.Clear();
            Held = 0;
            Leases = 0;
            StallsIgnored = 0;
        }

        /// <summary>Releases every engagement a leaving player held.</summary>
        internal static void ForgetPlayer(PlayerId player)
        {
            Expired.Clear();

            foreach (KeyValuePair<EnemyNetId, Engagement> entry in Engagements)
            {
                if (entry.Value.Player.Value == player.Value) Expired.Add(entry.Key);
            }

            foreach (EnemyNetId id in Expired)
            {
                End(id, Engagements[id]);
                Engagements.Remove(id);
            }
        }

        /// <summary>Applies what clients report and expires what they stopped reporting.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (CoopSession.State != CoopSessionState.Hosting)
            {
                if (Engagements.Count > 0) Reset();
                return;
            }

            while (TryTakeEnemyEngage(out EnemyEngageMessage message))
            {
                Apply(message);
            }

            if (Engagements.Count == 0) return;

            float step = unscaledDeltaTime;

            if (step > MaxAgeStepSeconds)
            {
                step = MaxAgeStepSeconds;
                StallsIgnored++;
            }

            Expired.Clear();

            foreach (KeyValuePair<EnemyNetId, Engagement> entry in Engagements)
            {
                entry.Value.SinceHeard += step;

                if (entry.Value.SinceHeard >= ExpirySeconds)
                {
                    Expired.Add(entry.Key);
                }
            }

            foreach (EnemyNetId id in Expired)
            {
                MelonLogger.Warning("[Engage] a client's engagement went quiet and was expired; " +
                    "the NPC was released.");

                End(id, Engagements[id]);
                Engagements.Remove(id);
            }
        }

        private static void Apply(in EnemyEngageMessage message)
        {
            try
            {
                if (!message.IsPlausible()) return;

                bool known = Engagements.TryGetValue(message.NetId, out Engagement? engagement);

                // First holder wins, so another player can neither take over nor end it.
                if (known && engagement!.Player.Value != message.Player.Value) return;

                if (message.Mode == EnemyEngageMessage.ModeNone)
                {
                    if (!known) return;

                    End(message.NetId, engagement!);
                    Engagements.Remove(message.NetId);
                    return;
                }

                if (known && engagement!.Mode == message.Mode)
                {
                    engagement.SinceHeard = 0f;
                    return;
                }

                // A mode change is an end and a begin.
                if (known)
                {
                    End(message.NetId, engagement!);
                }
                else
                {
                    engagement = new Engagement();
                    Engagements[message.NetId] = engagement;
                }

                engagement!.Player = message.Player;
                engagement.Mode = message.Mode;
                engagement.SinceHeard = 0f;
                engagement.HeldForDialogue = false;
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
            if (!EnemyRegistry.TryResolveOnHost(id, out Actor? actor)) return;

            // A cutscene walk is already the stand-still the talk wants, and the resets below would kill it.
            if (SceneMoveWatch.IsMoving(actor)) return;

            // Paused, not stopped: StartAI after StopAI restarts the routine from its first point.
            actor.AIModule?.PauseAI();

            // Pausing leaves the movement module running, so clear its path, direction and action too.
            actor.GetMovementModule()?._actorFollowPathMovement?.Reset();

            actor.GetMovementModule()?.SetDirection(UnityEngine.Vector2.zero);

            // A dash whose movement was reset never ends on its own.
            actor.GetActionModule()?.ClearCurrentAndQueuedActions();

            engagement.HeldForDialogue = true;

            if (engagement.Mode == EnemyEngageMessage.ModeDialogue)
            {
                Held++;
                MelonLogger.Msg("[Engage] an NPC was held still while a teammate talks to it.");
                return;
            }

            // Shielded only when not already invincible, so a quest's protection is never removed.
            Health? health = actor.GetHealth();

            if (health != null && !health.IsInvincible)
            {
                health.SetInvinciblity(true);
                engagement.Shielded = true;
            }

            // EnemyRegistry stops driving it by reading the lease.
            Leases++;
            MelonLogger.Msg("[Engage] a teammate's scripted brawl leased this NPC; its copy over there " +
                "fights for real and this one stands held.");
        }

        private static void End(EnemyNetId id, Engagement engagement)
        {
            try
            {
                if (!EnemyRegistry.TryResolveOnHost(id, out Actor? actor)) return;

                if (engagement.HeldForDialogue)
                {
                    actor.AIModule?.StartAI();
                    engagement.HeldForDialogue = false;
                }

                if (engagement.Shielded)
                {
                    Health? health = actor.GetHealth();
                    if (health != null) health.SetInvinciblity(false);
                    engagement.Shielded = false;
                }

                if (engagement.Mode == EnemyEngageMessage.ModeBrawl)
                {
                    // The relayed blows made this copy hostile, and only the remote brawl can undo its own alignments.
                    EnemyThreat.PardonAll(actor);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Engage] releasing an NPC from an engagement failed: " + e);
            }
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<EnemyEngageMessage>(MessageIds.EnemyEngage, OnEnemyEngage);
        }

        void ICoopSystem.EndSession()
        {
            IncomingEnemyEngage.Clear();
            IncomingHostEngage.Clear();
        }

        /// <summary>
        /// Tells the host this player is in a dialogue or brawl with one of its NPCs, or that it ended. Repeated
        /// while it lasts, and the host expires the silent.
        /// </summary>
        internal static void ReportEnemyEngage(EnemyNetId id, byte mode)
        {
            if (CoopSession.State != CoopSessionState.Joined) return;

            NetworkAccess.SendToAll(new EnemyEngageMessage
            {
                Player = CoopSession.LocalPlayerId,
                NetId = id,
                Mode = mode,
            });
        }

        /// <summary>Takes the next engagement a client reported. Host only.</summary>
        internal static bool TryTakeEnemyEngage(out EnemyEngageMessage message)
            => IncomingEnemyEngage.TryDequeue(out message);

        /// <summary>Takes the next engagement the host announced about itself. Clients only.</summary>
        internal static bool TryTakeHostEngage(out EnemyEngageMessage message)
            => IncomingHostEngage.TryDequeue(out message);

        /// <summary>Tells every client what the host player is doing with one of its NPCs. Mirror of <see cref="ReportEnemyEngage"/>.</summary>
        internal static void AnnounceEnemyEngage(EnemyNetId id, byte mode)
        {
            if (CoopSession.State != CoopSessionState.Hosting) return;

            NetworkAccess.SendToAll(new EnemyEngageMessage
            {
                Player = CoopSession.LocalPlayerId,
                NetId = id,
                Mode = mode,
            });
        }

        private static void OnEnemyEngage(INetPeer peer, EnemyEngageMessage message)
        {
            // The host's own engagements, so a teammate can join the fight.
            if (CoopSession.State == CoopSessionState.Joined)
            {
                if (!CoopSession.IsFromHost()) return;

                CoopSession.EnqueueLatest(IncomingHostEngage, message, CoopSession.MaxPendingDamage);
                return;
            }

            if (CoopSession.State != CoopSessionState.Hosting) return;

            if (!CoopSession.TryGetSender(peer, out PlayerId sender)) return;

            message.Player = sender;

            // A newer engagement statement supersedes an older one.
            CoopSession.EnqueueLatest(IncomingEnemyEngage, message, CoopSession.MaxPendingDamage);
        }

        void ICoopSystem.PlayerLeft(PlayerId player)
        {
            ForgetPlayer(player);
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (!loaded) Reset();
        }
    }
}
