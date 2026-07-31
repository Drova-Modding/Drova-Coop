using Drova_Modding_API;
using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using MelonLoader;
using System.Reflection;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Pays every player for every creature the session kills.
    ///
    /// **Experience is the one reward a shared world cannot let both players find.** Loot lies on the ground
    /// and either of them can walk to it. A level is a number in one save file, and nothing in the world puts
    /// it in the other. Left alone, whoever lands the killing blow levels and the other does not, and after a
    /// few hours the two are playing different games - the same camp is a fight for one of them and a
    /// massacre for the other, which is the point at which playing together stops working.
    ///
    /// **The full amount to everybody rather than a share.** Splitting is the obvious reading and it is the
    /// wrong one: the game's levels are paced against one player clearing the world once, so halving every
    /// kill leaves both players permanently under-levelled for what is in front of them. Two players killing
    /// twice as much between them is what keeps that pacing intact.
    ///
    /// **Exactly once per creature per player, and that is the whole difficulty.** Both machines run the
    /// death: the host's own kill fires the game's payout, and the client applying that reported death fires
    /// it again on its own machine - so a creature two players fought together already pays the client twice
    /// the moment anything is added on top. The handle is what makes that answerable. Whichever cause gets
    /// there first is recorded against it, and the game's own payout is then refused for a creature this
    /// player has already been paid for.
    ///
    /// It rides on <c>PlayerAttributeStats.CheckPlayerGainExp</c>, which is the game's single decision about
    /// whether a death is worth anything to the player - a public static that every kill funnels into through
    /// <c>AttributeStats.DiedListener</c>, which latches so it is asked once per body. Measured rather than
    /// predicted: what is reported is the change in the player's own total across that call, so the answer
    /// stays right without this file having to reimplement the rule that produced it.
    /// </summary>
    internal static class ExperienceShare
    {
        /// <summary>
        /// How many payouts wait for a player to arrive before further ones are refused.
        ///
        /// The wait is real and short: a payout can arrive while this machine is still loading its world, and
        /// there is no player to pay until it has one. Nothing repeats a payout, so these are held rather
        /// than dropped.
        /// </summary>
        private const int MaxWaiting = 512;

        /// <summary>
        /// One <c>CheckPlayerGainExp</c> call being watched.
        ///
        /// A stack rather than a field, because the call is not guaranteed to be the outermost thing
        /// happening: a payout raises the level-up event, and anything a listener does that kills something
        /// would land back in here. A single slot would then report the inner creature's figure against the
        /// outer creature's handle.
        /// </summary>
        private readonly struct Watch(Actor? owner, int before, bool measured)
        {
            internal Actor? Owner { get; } = owner;

            internal int Before { get; } = before;

            /// <summary>
            /// False when the prefix had nothing to compare against - no session, no player, or a payout it
            /// refused outright - in which case the postfix pops this and does nothing else.
            /// </summary>
            internal bool Measured { get; } = measured;
        }

        private static readonly Stack<Watch> _watching = new();

        // Creatures this player has been paid for, however the payment arrived. **The whole mechanism.**
        private static readonly HashSet<EnemyNetId> _paid = new();

        private static readonly List<ExperienceMessage> _waiting = new();

        private static int _sent;
        private static int _received;
        private static int _refusedLocal;
        private static int _unshareable;
        private static bool _hookFired;

        /// <summary>How many kills this machine has told the session about. For <c>coop_status</c>.</summary>
        internal static int Sent => _sent;

        /// <summary>How many kills this player was paid for on somebody else's word.</summary>
        internal static int Received => _received;

        /// <summary>
        /// How many times the game's own payout was refused because this player had already been paid for
        /// that creature. Expected to be non-zero whenever two players fight the same thing; it is the
        /// double payment that would otherwise happen.
        /// </summary>
        internal static int RefusedLocal => _refusedLocal;

        /// <summary>
        /// How many kills could not be shared because the creature was not part of the session - nobody had
        /// claimed it, so there is no handle both machines agree on.
        /// </summary>
        internal static int Unshareable => _unshareable;

        /// <summary>
        /// Whether the hook has ever run. Without it nothing is shared and nothing says so.
        /// </summary>
        internal static bool HookFired => _hookFired;

        /// <summary>How many payouts are waiting for a player to pay. For <c>coop_status</c>.</summary>
        internal static int Pending => _waiting.Count;

        /// <summary>
        /// Applies the patches. Called once at melon init.
        ///
        /// **Resolved by signature rather than by name, because the name alone is ambiguous.** There are two
        /// <c>CheckPlayerGainExp</c> methods in scope here: the static one this file wants, declared on
        /// <c>PlayerAttributeStats</c>, and the single-argument instance one inherited from
        /// <c>AttributeStats</c> that calls it. Asking for the name gets a match failure rather than either
        /// of them - and naming the signature is the honest thing to do in any case when two exist, since
        /// which one is meant would otherwise depend on how the resolver breaks the tie.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            MethodBase? target = HarmonyLib.AccessTools.Method(
                typeof(PlayerAttributeStats),
                nameof(PlayerAttributeStats.CheckPlayerGainExp),
                new[] { typeof(Actor), typeof(HealthChangeArgs), typeof(int) });

            const string Label = "PlayerAttributeStats.CheckPlayerGainExp";

            Hooking.TryPrefix(harmony, target, typeof(ExperienceShare), nameof(CheckPrefix), Label);
            Hooking.TryPostfix(harmony, target, typeof(ExperienceShare), nameof(CheckPostfix), Label);
        }

        /// <summary>
        /// Pays the local player for what everybody else killed. Call once per frame from a loaded world.
        ///
        /// **Before the reported deaths are applied**, so a creature whose payout and death arrive together
        /// is paid at the figure the machine that killed it calculated, and this machine's own death path
        /// then finds it already settled. Either order pays exactly once; this one pays the right amount.
        /// </summary>
        internal static void Update()
        {
            if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined)
            {
                Clear();
                return;
            }

            try
            {
                while (CoopSession.TryTakeExperience(out ExperienceMessage message))
                {
                    if (_waiting.Count >= MaxWaiting)
                    {
                        MelonLogger.Warning($"[Exp] more than {MaxWaiting} payouts are waiting for a player to " +
                            $"pay; {message.NetId} was dropped and that experience is lost.");
                        continue;
                    }

                    _waiting.Add(message);
                }

                if (_waiting.Count == 0) return;

                if (!TryGetLocalStats(out PlayerAttributeStats stats)) return;

                foreach (ExperienceMessage message in _waiting)
                {
                    Pay(stats, message);
                }

                _waiting.Clear();
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Exp] paying out what somebody else killed failed: " + e);
            }
        }

        /// <summary>
        /// Forgets what has been paid. Called when a session ends or the world goes away.
        ///
        /// **The record has to go with the session.** A handle means something only for as long as the
        /// session that handed it out, so carrying this into the next one would refuse payouts for creatures
        /// that merely inherited the same numbers.
        /// </summary>
        internal static void Clear()
        {
            _paid.Clear();
            _waiting.Clear();
            _watching.Clear();
        }

        private static void Pay(PlayerAttributeStats stats, in ExperienceMessage message)
        {
            if (!message.IsPlausible()) return;

            // Already settled, by this machine's own kill or by an earlier copy of this message.
            if (!_paid.Add(message.NetId)) return;

            _received++;

            stats.AddExperiencePoints(message.Amount);
        }

        // ------------------------------------------------------------------ the game's own payout

        /// <summary>
        /// Refuses the game's payout for a creature this player has already been paid for, and otherwise
        /// notes what the player has so the postfix can see what changed.
        /// </summary>
        /// <returns>False to skip the original, which is what stops a second payment.</returns>
        private static bool CheckPrefix(Actor owner, int expOnDead)
        {
            _hookFired = true;

            // **Pushed on every path, including the refusal, and that is not tidiness.** Harmony runs a
            // postfix even when a prefix returns false, so a path that skipped the push would leave the
            // postfix popping the entry belonging to whatever call this one is nested inside.
            bool measured = false;
            int before = 0;

            try
            {
                if (expOnDead > 0
                    && (CoopSession.State == CoopSessionState.Hosting || CoopSession.State == CoopSessionState.Joined))
                {
                    if (owner != null && EnemyRegistry.TryGetSessionId(owner, out EnemyNetId settled)
                        && _paid.Contains(settled))
                    {
                        _refusedLocal++;

                        _watching.Push(new Watch(null!, 0, false));
                        return false;
                    }

                    if (TryGetLocalStats(out PlayerAttributeStats stats))
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

            _watching.Push(new Watch(measured ? owner : null!, before, measured));
            return true;
        }

        /// <summary>
        /// Tells everybody what the creature turned out to be worth, when it was worth anything.
        /// </summary>
        private static void CheckPostfix()
        {
            try
            {
                // Empty only when the prefix did not apply. Every path through it pushes exactly one entry.
                if (_watching.Count == 0) return;

                Watch watch = _watching.Pop();
                if (!watch.Measured) return;

                if (!TryGetLocalStats(out PlayerAttributeStats stats)) return;

                // Measured rather than assumed to be expOnDead. The game's decision has several ways to
                // answer "nothing", and the change in the player's own total is the only reading of it that
                // cannot disagree with what actually happened.
                int gained = stats.ExperiencePoints - watch.Before;
                if (gained <= 0) return;

                if (watch.Owner == null) return;

                if (!EnemyRegistry.TryGetSessionId(watch.Owner, out EnemyNetId netId))
                {
                    // Not part of the session, so there is no name for it that the other machine would
                    // recognise. This player keeps what they earned; nobody else hears about it.
                    _unshareable++;
                    return;
                }

                // Recorded before it is sent, so a payout coming back for the same creature - or this
                // machine's own death path reaching the same creature again after a rebuild - is refused.
                if (!_paid.Add(netId)) return;

                _sent++;

                CoopSession.BroadcastExperience(netId, gained);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Exp] telling the session what a creature was worth failed: " + e);
            }
        }

        private static bool TryGetLocalStats(out PlayerAttributeStats stats)
        {
            stats = null!;

            if (!PlayerAccess.TryGetPlayer(out Actor player) || player == null) return false;

            AttributeStats attributes = player.GetStats();
            if (attributes == null) return false;

            stats = attributes.TryCast<PlayerAttributeStats>()!;

            return stats != null;
        }
    }
}
