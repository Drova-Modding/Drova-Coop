using Drova_Modding_API.Access;
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
    /// Lets a creature think on the machine of the player it fights, so its attacks reach that player without the link's
    /// delay. The host still owns its health and still speaks for it to everyone else: <see cref="LeaseMirror"/> drives the
    /// host's copy from the fighter's, and <see cref="EnemyBroadcaster"/> passes that on.
    /// </summary>
    internal sealed class FighterDrive : ICoopSystem
    {
        private const float SweepSeconds = 0.25f;

        /// <summary>EnemyRegistry's release range, inside which a client has the creature's body loaded.</summary>
        private const float Reach = 700f;

        /// <summary>How long a fighter may say nothing, or have no target yet, before the host thinks for the creature again.</summary>
        private const float QuietSeconds = 1.5f;

        /// <summary>How long a creature taken back stays with the host, so two machines' AIs cannot pass it back and forth.</summary>
        private const float RestSeconds = 3f;

        private sealed class Grant
        {
            public PlayerId Fighter;
            public float Since;
            public float HeardAt = -1f;
            public PlayerId Target = PlayerId.None;
            public bool Fighting;
        }

        private static readonly Dictionary<EnemyNetId, Grant> Grants = new();

        /// <summary>A change wanted on the last sweep. It is made when the next sweep still wants it.</summary>
        private static readonly Dictionary<EnemyNetId, PlayerId> Candidates = new();
        private static readonly Dictionary<EnemyNetId, float> RestUntil = new();

        /// <summary>Whom a creature taken back was fighting, for the host's AI to pick up.</summary>
        private static readonly Dictionary<EnemyNetId, PlayerId> ResumeTargets = new();
        private static readonly List<EnemyNetId> Lost = new();

        /// <summary>Client: the creatures this machine thinks for.</summary>
        private static readonly HashSet<EnemyNetId> Mine = new();

        /// <summary>Client: whether the host last said each of them is stunned.</summary>
        private static readonly HashSet<EnemyNetId> Stunned = new();

        private static float _sinceSweep;

        /// <summary>Creatures handed to the client they fight. For tests.</summary>
        internal static int Granted { get; private set; }

        /// <summary>Creatures taken back by the host. For tests.</summary>
        internal static int TakenBack { get; private set; }

        /// <summary>Creatures this client thinks for right now. For tests.</summary>
        internal static int MineCount => Mine.Count;

        string? ICoopSystem.HookSwitch => null;

        /// <summary>Client: whether this machine thinks for the creature.</summary>
        internal static bool IsMine(EnemyNetId id) => Mine.Contains(id);

        /// <summary>Host: the client thinking for a creature, if any.</summary>
        internal static bool TryGetFighter(EnemyNetId id, out PlayerId fighter)
        {
            bool granted = Grants.TryGetValue(id, out Grant? grant);
            fighter = granted ? grant!.Fighter : PlayerId.None;
            return granted;
        }

        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (!CoopSession.JoinedWorld && Mine.Count > 0) ForgetMine();

            if (!CoopSession.HostsWorld)
            {
                if (Grants.Count > 0 || RestUntil.Count > 0) ForgetGrants();
                return;
            }

            _sinceSweep += unscaledDeltaTime;
            if (_sinceSweep < SweepSeconds) return;
            _sinceSweep = 0f;

            try
            {
                Sweep();
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Fighter] deciding which machine thinks for a creature failed: " + e);
            }
        }

        private static void Sweep()
        {
            foreach (EnemyNetId id in EnemyRegistry.ClaimedIds)
            {
                if (!EnemyRegistry.TryResolveOnHost(id, out Actor? actor) || !EnemyRegistry.IsReady(actor)) continue;

                Grants.TryGetValue(id, out Grant? grant);
                PlayerId holder = grant?.Fighter ?? PlayerId.Host;
                PlayerId wanted = Choose(id, actor, grant, out PlayerId target);

                if (wanted == holder)
                {
                    Candidates.Remove(id);
                    continue;
                }

                if (!Candidates.TryGetValue(id, out PlayerId candidate) || candidate != wanted)
                {
                    Candidates[id] = wanted;
                    continue;
                }

                Candidates.Remove(id);
                Move(id, holder, wanted, target);
            }

            Lost.Clear();
            foreach (EnemyNetId id in Grants.Keys)
            {
                if (!EnemyRegistry.ClaimedIds.Contains(id)) Lost.Add(id);
            }

            foreach (EnemyNetId id in Lost) Move(id, Grants[id].Fighter, PlayerId.Host, PlayerId.None);
        }

        /// <summary>Who should think for a creature: the player it fights, while the host is near enough to mirror it.</summary>
        private static PlayerId Choose(EnemyNetId id, Actor actor, Grant? grant, out PlayerId target)
        {
            target = PlayerId.None;
            float now = Time.unscaledTime;

            if (!HookSwitches.IsOn(HookSwitches.FighterDrive) || !EnemyRegistry.DrivesOnHost(id) || !MayLeaveHost(id, actor)) return PlayerId.Host;

            if (grant == null)
            {
                if (RestUntil.TryGetValue(id, out float until) && now < until) return PlayerId.Host;

                target = EnemyBroadcaster.TargetOf(actor);
                return target.IsAssigned && !target.IsHost && IsInReach(target, actor) && Fights(actor, target) ? target : PlayerId.Host;
            }

            if (!IsInReach(grant.Fighter, actor)) return PlayerId.Host;

            // Its AI was reset on the handover and needs a moment to pick the fight up.
            bool settling = now - grant.Since < QuietSeconds;
            if (grant.HeardAt < grant.Since) return settling ? grant.Fighter : PlayerId.Host;
            if (now - grant.HeardAt > QuietSeconds) return PlayerId.Host;

            target = grant.Target;
            if (target.IsHost) return PlayerId.Host;
            if (target.IsAssigned && IsInReach(target, actor)) return target;

            return grant.Fighting || settling ? grant.Fighter : PlayerId.Host;
        }

        /// <summary>Scripted fights, talks and scene walks stay with the host, which is where their scripts run.</summary>
        private static bool MayLeaveHost(EnemyNetId id, Actor actor)
        {
            if (EnemyEngagements.IsHeld(id) || SceneMoveWatch.IsMoving(actor)) return false;
            if (actor.CustomStats != null && (actor.CustomStats.IsInBrawl || actor.CustomStats.IsKnockout)) return false;

            Health? health = actor.GetHealth();
            return health != null && !health.IsDead;
        }

        /// <summary>
        /// Whether a creature is in combat with a player as its enemy. A follower or an escort also has the player as its
        /// target, and its routine and the scripts around it run on the host.
        /// </summary>
        private static bool Fights(Actor actor, PlayerId player)
        {
            if (actor.AIModule == null || !actor.AIModule.IsInCombat()) return false;
            if (!RemotePlayers.Bodies.TryGetValue(player, out RemotePlayer? remote) || remote.Body == null) return false;

            Il2CppDrova.Alignment.IAlignment? alignment = actor.Alignment;
            return alignment != null && alignment.CheckAlignment(
                new Il2CppDrova.Alignment.AlignmentArgs(remote.Body).Cast<Il2CppDrova.Alignment.IAlignmentArgs>(),
                Il2CppDrova.Alignment.AlignmentState.Enemy);
        }

        private static bool IsInReach(PlayerId player, Actor actor)
        {
            if (player.IsHost) return true;
            if (!RemotePlayers.Bodies.TryGetValue(player, out RemotePlayer? remote) || remote.Body == null) return false;

            return ((Vector2)(actor.transform.position - remote.Body.transform.position)).sqrMagnitude <= Reach * Reach;
        }

        private static void Move(EnemyNetId id, PlayerId from, PlayerId to, PlayerId target)
        {
            if (!from.IsHost) CoopSession.TrySendToPlayer(new EnemyFighterMessage { NetId = id, Yours = false }, from);

            if (to.IsHost)
            {
                Grants.Remove(id);
                ResumeTargets[id] = target;
                RestUntil[id] = Time.unscaledTime + RestSeconds;
                TakenBack++;
            }
            else
            {
                Grants[id] = new Grant { Fighter = to, Since = Time.unscaledTime };
                CoopSession.TrySendToPlayer(new EnemyFighterMessage { NetId = id, Yours = true }, to);
                Granted++;
            }

            MelonLogger.Msg($"[Fighter] {id} now thinks on " + (to.IsHost ? "the host." : $"player {to.Value}'s machine, whom it fights."));
        }

        /// <summary>Host: takes what a fighter says about a creature it thinks for.</summary>
        /// <returns>False when the sender does not think for it, so its record must not be applied.</returns>
        internal static bool Hear(EnemyNetId id, PlayerId sender, in EnemySnapshotMessage.Record record)
        {
            if (!Grants.TryGetValue(id, out Grant? grant) || grant.Fighter != sender) return false;

            grant.HeardAt = Time.unscaledTime;
            grant.Target = record.Target;
            grant.Fighting = (record.Flags & EnemySnapshotMessage.FlagInCombat) != 0;
            return true;
        }

        /// <summary>Host: points a creature just taken back at whoever it was fighting, since the hand-back reset its AI.</summary>
        internal static void Resume(EnemyNetId id, Actor body)
        {
            if (!ResumeTargets.Remove(id, out PlayerId target) || !target.IsAssigned) return;

            Actor? fought = null;
            if (target.IsHost) PlayerAccess.TryGetPlayer(out fought);
            else if (RemotePlayers.Bodies.TryGetValue(target, out RemotePlayer? remote)) fought = remote.Body;

            if (fought != null) EnemyThreat.Aggro(body, fought, EnemyRegistry.GuidOf(fought));
        }

        /// <summary>Host: tells a creature's fighter that somebody else struck it, since the blow landed on the host's copy.</summary>
        internal static void TellFighter(EnemyNetId id, PlayerId attacker, int amount)
        {
            if (!Grants.TryGetValue(id, out Grant? grant) || grant.Fighter == attacker) return;

            RemoteEnemyHealth.SendThreat(grant.Fighter, id, amount, attacker);
        }

        /// <summary>Client: makes a creature this machine thinks for notice a teammate who struck it.</summary>
        internal static void TakeThreat(in EnemyDamageMessage report)
        {
            if (!IsMine(report.NetId) || !EnemyRegistry.TryGetBody(report.NetId, out Actor? body)) return;

            Actor? attacker = RemoteEnemyHealth.AttackerBody(report.Attacker);
            if (attacker == null) return;

            string guid = EnemyRegistry.GuidOf(attacker);
            EnemyThreat.File(body, guid, report.Amount);
            EnemyThreat.Aggro(body, attacker, guid);
        }

        /// <summary>Client: starts the fight on a body whose AI was just switched back on for this player.</summary>
        internal static void Engage(Actor body)
        {
            if (PlayerAccess.TryGetPlayer(out Actor? local)) EnemyThreat.Aggro(body, local, EnemyRegistry.GuidOf(local));
        }

        /// <summary>
        /// Client: stuns a creature this machine thinks for when the host's copy is. Blows are counted on the host, so its
        /// poise breaks there.
        /// </summary>
        internal static void TakeStun(in EnemySnapshotMessage.Record record, Actor body)
        {
            bool stunned = (record.ModeAndTarget & EnemySnapshotMessage.PoiseBroken) != 0;

            if (!stunned)
            {
                Stunned.Remove(record.NetId);
                return;
            }

            if (Stunned.Add(record.NetId)) PuppetPoise.Break(body.PoiseController);
        }

        /// <summary>Client: the host released the creature.</summary>
        internal static void Forget(EnemyNetId id)
        {
            Mine.Remove(id);
            Stunned.Remove(id);
        }

        private static void ForgetMine()
        {
            Mine.Clear();
            Stunned.Clear();
        }

        private static void ForgetGrants()
        {
            Grants.Clear();
            Candidates.Clear();
            RestUntil.Clear();
            ResumeTargets.Clear();
            _sinceSweep = 0f;
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<EnemyFighterMessage>(MessageIds.EnemyFighter, (_, message) =>
            {
                if (!CoopSession.JoinedWorld || !CoopSession.IsFromHost()) return;

                if (message.Yours) Mine.Add(message.NetId);
                else Forget(message.NetId);
            });
        }

        void ICoopSystem.PlayerLeft(PlayerId player)
        {
            Lost.Clear();
            foreach (KeyValuePair<EnemyNetId, Grant> grant in Grants)
            {
                if (grant.Value.Fighter == player) Lost.Add(grant.Key);
            }

            foreach (EnemyNetId id in Lost) Move(id, player, PlayerId.Host, PlayerId.None);
        }

        void ICoopSystem.EndSession()
        {
            ForgetMine();
            ForgetGrants();
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (loaded) return;

            ForgetMine();
            ForgetGrants();
        }
    }
}
