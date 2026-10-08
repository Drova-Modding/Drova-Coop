using Drova_Modding_API;
using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Enemies;
using DrovaCoopMod.Systems.Loot.Patches;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using MelonLoader;
using System.Reflection;

namespace DrovaCoopMod.Systems.Loot
{
    /// <summary>
    /// Pays every player the full experience for every creature the session kills, exactly once per creature.
    /// Hooks <c>PlayerAttributeStats.CheckPlayerGainExp</c>, the single funnel for kill payouts, and reports the measured change in total.
    /// </summary>
    internal sealed class ExperienceShare : ICoopSystem
    {
        // Payouts can arrive while the world is still loading, and nothing repeats them.
        private const int MaxWaiting = 512;

        /// <summary>Unpaid kills held before further ones are refused. Nothing restates a payout.</summary>
        private const int MaxPendingExperience = 512;

        private static readonly Queue<ExperienceMessage> IncomingExperience = new();

        // A stack because a level-up listener can kill something and reenter the hook.
        private static readonly Stack<Watch> Watching = new();

        // Creatures this player has been paid for, however the payment arrived.
        private static readonly HashSet<EnemyNetId> Paid = new();

        private static readonly List<ExperienceMessage> Waiting = new();

        internal static int Sent { get; private set; }

        internal static int Received { get; private set; }

        /// <summary>How many local payouts were refused as already paid. Non-zero whenever two players fight the same thing.</summary>
        internal static int RefusedLocal { get; private set; }

        /// <summary>How many kills were not shared because the creature was never claimed by the session.</summary>
        internal static int Unshareable { get; private set; }

        internal static bool HookFired { get; private set; }

        internal static int Pending => Waiting.Count;

        /// <summary>Applies the patches, by signature since an inherited instance overload shares the name.</summary>
        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            MethodBase? target = HarmonyLib.AccessTools.Method(
                typeof(PlayerAttributeStats),
                nameof(PlayerAttributeStats.CheckPlayerGainExp),
                [typeof(Actor), typeof(HealthChangeArgs), typeof(int)]);

            const string label = "PlayerAttributeStats.CheckPlayerGainExp";

            Hooking.TryPrefix(harmony, target, typeof(ExperienceSharePatches), nameof(ExperienceSharePatches.CheckPrefix), label);
            Hooking.TryPostfix(harmony, target, typeof(ExperienceSharePatches), nameof(ExperienceSharePatches.CheckPostfix), label);
        }

        /// <summary>
        /// Pays the local player for what everybody else killed. Runs before reported deaths are applied, so the killer's figure wins.
        /// </summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (!CoopSession.IsLive)
            {
                Clear();
                return;
            }

            try
            {
                while (TryTakeExperience(out ExperienceMessage message))
                {
                    if (Waiting.Count >= MaxWaiting)
                    {
                        MelonLogger.Warning($"[Exp] more than {MaxWaiting} payouts are waiting for a player to " +
                            $"pay; {message.NetId} was dropped and that experience is lost.");
                        continue;
                    }

                    Waiting.Add(message);
                }

                if (Waiting.Count == 0) return;

                PlayerAttributeStats? stats = PlayerAccess.GetPlayerAttributeStats();
                if (stats == null) return;

                foreach (ExperienceMessage message in Waiting)
                {
                    Pay(stats, message);
                }

                Waiting.Clear();
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Exp] paying out what somebody else killed failed: " + e);
            }
        }

        /// <summary>Forgets what has been paid, since net ids mean nothing outside their session.</summary>
        private static void Clear()
        {
            Paid.Clear();
            Waiting.Clear();
            Watching.Clear();
        }

        private static void Pay(PlayerAttributeStats stats, in ExperienceMessage message)
        {
            if (!message.IsPlausible()) return;

            if (!Paid.Add(message.NetId)) return;

            Received++;

            stats.AddExperiencePoints(message.Amount);
        }

        // Refuses an already-paid creature, otherwise records the total so the postfix can measure the change.
        internal static bool CheckPrefix(Actor owner, int expOnDead)
        {
            HookFired = true;

            // Every path pushes exactly one entry, because the postfix always pops one.
            bool measured = false;
            int before = 0;

            try
            {
                if (expOnDead > 0
                    && CoopSession.IsLive)
                {
                    if (owner != null && EnemyRegistry.TryGetSessionId(owner, out EnemyNetId settled)
                        && Paid.Contains(settled))
                    {
                        RefusedLocal++;

                        Watching.Push(new Watch(null, 0));
                        return false;
                    }

                    PlayerAttributeStats? stats = PlayerAccess.GetPlayerAttributeStats();
                    if (stats != null)
                    {
                        measured = true;
                        before = stats.ExperiencePoints;
                    }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Exp] deciding whether this player has already been paid failed: " + e);

                measured = false;
            }

            Watching.Push(new Watch(measured ? owner : null, before));
            return true;
        }

        internal static void CheckPostfix()
        {
            try
            {
                if (Watching.Count == 0) return;

                Watch watch = Watching.Pop();
                if (watch.Owner == null) return;

                PlayerAttributeStats? stats = PlayerAccess.GetPlayerAttributeStats();
                if (stats == null) return;

                // Measured, not expOnDead: the game has several ways to pay nothing.
                int gained = stats.ExperiencePoints - watch.Before;
                if (gained <= 0) return;

                if (!EnemyRegistry.TryGetSessionId(watch.Owner, out EnemyNetId netId))
                {
                    Unshareable++;
                    return;
                }

                // Recorded before sending, so an echoed payout or a rebuilt body's death is refused.
                if (!Paid.Add(netId)) return;

                Sent++;

                BroadcastExperience(netId, gained);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Exp] telling the session what a creature was worth failed: " + e);
            }
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<ExperienceMessage>(MessageIds.Experience, OnExperience);
        }

        void ICoopSystem.EndSession()
        {
            IncomingExperience.Clear();
        }

        /// <summary>Tells everyone what a creature was worth, so they can be paid for it too.</summary>
        private static void BroadcastExperience(EnemyNetId netId, int amount)
        {
            if (!CoopSession.IsLive) return;
            if (netId == EnemyNetId.None || amount <= 0) return;

            ExperienceMessage message = new()
            {
                Earner = CoopSession.LocalPlayerId,
                NetId = netId,
                Amount = amount,
            };

            NetworkAccess.SendToAll(message);
        }

        /// <summary>Takes the next creature somebody else was paid for that this player has not been.</summary>
        private static bool TryTakeExperience(out ExperienceMessage message)
            => IncomingExperience.TryDequeue(out message);

        private static void OnExperience(INetPeer peer, ExperienceMessage message)
        {
            if (!CoopSession.IsLive) return;
            if (!message.IsPlausible()) return;

            if (NetworkAccess.Role == NetRole.Host)
            {
                if (!CoopSession.TryGetSender(peer, out PlayerId sender)) return;

                message = message with { Earner = sender };

                CoopSession.SendToClientsExcept(message, peer.Id);
            }
            else if (!CoopSession.IsFromHost() || message.Earner == CoopSession.LocalPlayerId)
            {
                return;
            }

            // Refused rather than dropping the oldest: nothing restates a payout.
            if (!CoopSession.Enqueue(IncomingExperience, message, MaxPendingExperience))
            {
                MelonLogger.Warning($"[Exp] more than {MaxPendingExperience} kills are waiting to be paid out; " +
                    $"{message.NetId} from {message.Earner} was refused and that experience is lost.");
            }
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (!loaded) Clear();
        }

        private readonly struct Watch(Actor? owner, int before)
        {
            // Null when the postfix should do nothing.
            internal Actor? Owner { get; } = owner;

            internal int Before { get; } = before;
        }
    }
}
