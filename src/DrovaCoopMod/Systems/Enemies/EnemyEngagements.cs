using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.Systems.World;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.Enemies
{
    /// <summary>
    /// The host's side of a client's engagement with one of its NPCs. A dialogue holds the NPC still, and a brawl
    /// leases it to the client. Engagements expire unless repeated.
    /// </summary>
    internal sealed class EnemyEngagements : ICoopSystem
    {
        /// <summary>Three missed keepalives at the client's five-second repeat.</summary>
        private const float ExpirySeconds = 15f;

        /// <summary>A long stall must not age every engagement at once, since its keepalives were just drained.</summary>
        private const float MaxAgeStepSeconds = 1f;

        /// <summary>EnemyRegistry's release range, past which the client has no body of the NPC to fight or talk to.</summary>
        private const float EngageReach = 700f;

        private static readonly Queue<EnemyEngageMessage> IncomingEnemyEngage = new();
        private static readonly Queue<EnemyEngageMessage> IncomingHostEngage = new();

        private sealed class Engagement
        {
            public PlayerId Player;
            public byte Mode;
            public float SinceHeard;
            public bool HeldForDialogue;
            public bool Shielded;
            public bool Ally;
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

        /// <summary>Host: whether a spot is within <paramref name="range"/> of a creature whose brawl is leased to this player.</summary>
        internal static bool IsNearLeaseOf(PlayerId player, Vector2 at, float range)
        {
            foreach (KeyValuePair<EnemyNetId, Engagement> entry in Engagements)
            {
                if (entry.Value.Mode != EnemyEngageMessage.ModeBrawl || entry.Value.Player.Value != player.Value) continue;
                if (!EnemyRegistry.TryResolveOnHost(entry.Key, out Actor? actor)) continue;

                if (((Vector2)actor.transform.position - at).sqrMagnitude <= range * range) return true;
            }

            return false;
        }

        /// <summary>Host: whether a client's talk or scripted fight runs within <paramref name="range"/> of a spot.</summary>
        internal static bool IsAnyNear(Vector2 at, float range)
        {
            foreach (EnemyNetId id in Engagements.Keys)
            {
                if (!EnemyRegistry.TryResolveOnHost(id, out Actor? actor)) continue;

                if ((actor.GetFeetPosition() - at).sqrMagnitude <= range * range) return true;
            }

            return false;
        }

        /// <summary>Whether a player stands within <see cref="EngageReach"/> of the creature. A summon leased by <see cref="Lease"/> is near by construction.</summary>
        private static bool IsNearPlayer(EnemyNetId id, PlayerId player)
        {
            if (player.Value == CoopSession.LocalPlayerId.Value) return true;
            if (!EnemyRegistry.TryResolveOnHost(id, out Actor? actor) || actor == null) return true;
            if (!RemotePlayers.Bodies.TryGetValue(player, out RemotePlayer? other) || other.Body == null) return false;

            return ((Vector2)(actor.transform.position - other.Body.transform.position)).sqrMagnitude <= EngageReach * EngageReach;
        }

        /// <summary>Leases a creature, such as a summon of their fight, to a player as if they had reported its brawl.</summary>
        internal static void Lease(EnemyNetId id, PlayerId player)
        {
            Apply(new EnemyEngageMessage { NetId = id, Player = player, Mode = EnemyEngageMessage.ModeBrawl });
        }

        /// <summary>Releases every engagement.</summary>
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
            if (!CoopSession.HostsWorld)
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

                // The host brawling this NPC holds it first, since a lease would freeze it mid-fight here.
                if (!known && message.Mode != EnemyEngageMessage.ModeNone && InHostsBrawl(message.NetId)) return;

                // A lease hands the NPC's fate to that player, so only one standing where its body could be loaded gets it.
                if (!known && message.Mode != EnemyEngageMessage.ModeNone && !IsNearPlayer(message.NetId, message.Player)) return;

                Forward(message);

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
                engagement.Ally = message.Ally;

                Begin(message.NetId, engagement);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Engage] applying a client's engagement failed: " + e);
            }
        }

        /// <summary>
        /// Tells the other clients about a client's talk or brawl, so the NPC is busy for them too and, in a brawl,
        /// their copies take their players' swings as for the host's own.
        /// </summary>
        private static void Forward(in EnemyEngageMessage message)
        {
            foreach (PlayerId player in CoopSession.Players.Keys)
            {
                if (player != message.Player && player != CoopSession.LocalPlayerId) CoopSession.TrySendToPlayer(message, player);
            }
        }

        private static bool InHostsBrawl(EnemyNetId id)
        {
            if (!EnemyRegistry.TryResolveOnHost(id, out Actor? actor) || actor.CustomStats == null || !actor.CustomStats.IsInBrawl) return false;
            return PlayerAccess.TryGetPlayer(out Actor host) && host.CustomStats != null && host.CustomStats.IsInBrawl;
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

            actor.GetMovementModule()?.SetDirection(Vector2.zero);

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

            // The game's hit filter passes only an enemy's swings, so the host player could not join the fight. End pardons it.
            // Not for an NPC on the teammate's side, which the host player must not be able to strike.
            if (!engagement.Ally && PlayerAccess.TryGetPlayer(out Actor host)) EnemyThreat.Aggro(actor, host, EnemyRegistry.GuidOf(host));

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
        /// Tells the host this player is in a dialogue or brawl with one of its NPCs, or that it ended. Repeated while it lasts.
        /// </summary>
        internal static void ReportEnemyEngage(EnemyNetId id, byte mode, bool ally = false)
        {
            if (!CoopSession.JoinedWorld) return;

            NetworkAccess.SendToAll(new EnemyEngageMessage
            {
                Player = CoopSession.LocalPlayerId,
                NetId = id,
                Mode = mode,
                Ally = ally,
            });
        }

        /// <summary>Takes the next engagement a client reported. Host only.</summary>
        internal static bool TryTakeEnemyEngage(out EnemyEngageMessage message)
            => IncomingEnemyEngage.TryDequeue(out message);

        /// <summary>Takes the next engagement the host announced about itself. Clients only.</summary>
        internal static bool TryTakeHostEngage(out EnemyEngageMessage message)
            => IncomingHostEngage.TryDequeue(out message);

        /// <summary>Tells every client what the host player is doing with one of its NPCs. Mirror of <see cref="ReportEnemyEngage"/>.</summary>
        internal static void AnnounceEnemyEngage(EnemyNetId id, byte mode, bool ally = false)
        {
            if (!CoopSession.HostsWorld) return;

            NetworkAccess.SendToAll(new EnemyEngageMessage
            {
                Player = CoopSession.LocalPlayerId,
                NetId = id,
                Mode = mode,
                Ally = ally,
            });
        }

        private static void OnEnemyEngage(INetPeer peer, EnemyEngageMessage message)
        {
            // The host's own engagements, so a teammate can join the fight.
            if (CoopSession.JoinedWorld)
            {
                if (!CoopSession.IsFromHost()) return;

                CoopSession.EnqueueLatest(IncomingHostEngage, message, CoopSession.MaxPendingDamage);
                return;
            }

            if (!CoopSession.HostsWorld) return;

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
