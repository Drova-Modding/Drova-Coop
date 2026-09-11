using Il2CppDrova.Saveables;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Remembers, in this savegame, which dialogue nodes have already paid this player.
    ///
    /// The two dialogue shares deduplicate by node identity, so a handover fills each player's hands
    /// exactly once however many copies of the conversation run. Those sets live in memory and a
    /// savegame load clears them - so a reload forgets, the teammate whose copy of the conversation
    /// never ran takes the branch, their grant node fires against an empty set, and the party ends up
    /// with two of an authored one-off. Every reload, forever.
    ///
    /// The identity is already savegame-stable - the dialogue tree's name plus the authored node id,
    /// neither of which is minted at runtime - so the only thing missing was somewhere to put it. Here
    /// it goes into the game's own flat savegame dictionary under a mod-owned prefix. The game never
    /// reads those keys, and a save written before this existed simply has none of them.
    ///
    /// This is deliberately not the same thing as sharing which one-time dialogue branches a player has
    /// taken. That was designed and rejected: it deletes hundreds of one-off branches from a joining
    /// player's own savegame, permanently. What duplicates is not the branch, it is the payment, and
    /// this remembers exactly the payment.
    /// </summary>
    internal sealed class GrantLedger
    {
        private readonly string _prefix;
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        private int _remembered;
        private int _hits;
        private int _refusedWrites;

        /// <param name="prefix">
        /// The savegame key prefix. One per subsystem rather than one shared namespace, so an item
        /// node and a reward node can never collide on a node id they both happen to carry.
        /// </param>
        internal GrantLedger(string prefix)
        {
            _prefix = prefix;
        }

        /// <summary>How many nodes were written into this savegame. For <c>coop_status</c>.</summary>
        internal int Remembered => _remembered;

        /// <summary>How many repeats this savegame's memory caught. For <c>coop_status</c>.</summary>
        internal int Hits => _hits;

        /// <summary>
        /// How many notes the game refused to store. Anything above zero means a handover can still be
        /// paid a second time after a reload, and says so rather than looking healthy.
        /// </summary>
        internal int RefusedWrites => _refusedWrites;

        /// <summary>Forgets this session's cache. The savegame's own record is untouched.</summary>
        internal void Reset()
        {
            _seen.Clear();
            _remembered = 0;
            _hits = 0;
            _refusedWrites = 0;
        }

        /// <summary>
        /// Whether this savegame has already seen this node pay.
        /// </summary>
        /// <param name="identity">The node, as the dialogue shares name it.</param>
        internal bool WasPaid(string identity)
        {
            if (string.IsNullOrEmpty(identity)) return false;
            if (_seen.Contains(identity)) return true;

            try
            {
                Savegame save = Savegame.Current;
                if (save?.Data == null) return false;

                bool paid = false;
                save.Data.GetBool(_prefix + identity, ref paid);

                if (paid)
                {
                    _seen.Add(identity);
                    _hits++;
                }

                return paid;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Grants] reading this savegame's dialogue ledger failed: " + e);
                return false;
            }
        }

        /// <summary>
        /// Records that this node has paid.
        ///
        /// Call only where the goods demonstrably landed. A node remembered after a grant that quietly
        /// failed is an item the player never got and can now never get, which is worse than the
        /// duplicate this exists to prevent.
        /// </summary>
        /// <param name="identity">The node, as the dialogue shares name it.</param>
        internal void Remember(string identity)
        {
            if (string.IsNullOrEmpty(identity)) return;
            if (!_seen.Add(identity)) return;

            try
            {
                Savegame save = Savegame.Current;
                if (save?.Data == null) return;

                string key = _prefix + identity;
                save.Data.SetBool(key, true);

                // Read back rather than trust the call. The savegame drops a write outright while the
                // game is not accepting them - which it is not for the whole of a scene load - and says
                // nothing about it. A note that never landed leaves the duplicate live after the next
                // reload, which is precisely the failure this class exists to end.
                bool landed = false;
                save.Data.GetBool(key, ref landed);

                if (!landed)
                {
                    _refusedWrites++;

                    MelonLogger.Warning("[Grants] this savegame would not accept a note that a dialogue " +
                        "handover was already paid; the game is refusing savegame writes just now. It " +
                        "may be paid a second time after a reload.");

                    return;
                }

                _remembered++;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Grants] remembering a dialogue handover in this savegame failed: " + e);
            }
        }
    }
}
