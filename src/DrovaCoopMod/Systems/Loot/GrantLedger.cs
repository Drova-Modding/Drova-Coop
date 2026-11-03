using Il2CppDrova.Saveables;
using Il2CppNodeCanvas.DialogueTrees;
using Il2CppNodeCanvas.Framework;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.Loot
{
    /// <summary>
    /// Remembers, in this session and in this savegame, which dialogue nodes have already paid this player,
    /// so a reload cannot pay an authored one-off twice. Stored under a mod-owned prefix in the savegame's flat dictionary.
    /// </summary>
    internal sealed class GrantLedger
    {
        private readonly string _prefix;

        /// <summary>Claimed this session or found in the savegame. The savegame is only asked about what is not in here.</summary>
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        /// <summary>One prefix per subsystem, so item and reward nodes never collide on a node id.</summary>
        internal GrantLedger(string prefix)
        {
            _prefix = prefix;
        }

        /// <summary>How many nodes were written into this savegame. For <c>coop_status</c>.</summary>
        internal int Remembered { get; private set; }

        /// <summary>How many repeats this savegame's memory caught. For <c>coop_status</c>.</summary>
        internal int Hits { get; private set; }

        /// <summary>How many notes the game refused to store. Above zero, a handover may pay twice after a reload.</summary>
        internal int RefusedWrites { get; private set; }

        /// <summary>Forgets this session's cache. The savegame's own record is untouched.</summary>
        internal void Reset()
        {
            _seen.Clear();
            Remembered = 0;
            Hits = 0;
            RefusedWrites = 0;
        }

        /// <summary>Whether this session already knows the node paid, without asking the savegame.</summary>
        internal bool IsKnown(string identity)
        {
            return _seen.Contains(identity);
        }

        /// <summary>Marks the node paid for this session, before the goods are handed out. Does not write the savegame.</summary>
        internal void Claim(string identity)
        {
            _seen.Add(identity);
        }

        /// <summary>Whether this savegame has already seen this node pay.</summary>
        internal bool WasPaid(string? identity)
        {
            if (string.IsNullOrEmpty(identity)) return false;
            if (_seen.Contains(identity)) return true;

            try
            {
                Savegame? save = Savegame.Current;
                if (save?.Data == null) return false;

                bool paid = false;
                save.Data.GetBool(_prefix + identity, ref paid);

                if (paid)
                {
                    _seen.Add(identity);
                    Hits++;
                }

                return paid;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Grants] reading this savegame's dialogue ledger failed: " + e);
                return false;
            }
        }

        /// <summary>Records in the savegame that this node has paid. Call only where the goods demonstrably landed.</summary>
        internal void Remember(string? identity)
        {
            if (string.IsNullOrEmpty(identity)) return;
            _seen.Add(identity);

            try
            {
                Savegame? save = Savegame.Current;
                if (save?.Data == null) return;

                string key = _prefix + identity;
                save.Data.SetBool(key, true);

                // Read back, since the savegame silently drops writes during a scene load.
                bool landed = false;
                save.Data.GetBool(key, ref landed);

                if (!landed)
                {
                    RefusedWrites++;

                    MelonLogger.Warning("[Grants] this savegame would not accept a note that a dialogue " +
                        "handover was already paid; the game is refusing savegame writes just now. It " +
                        "may be paid a second time after a reload.");

                    return;
                }

                Remembered++;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Grants] remembering a dialogue handover in this savegame failed: " + e);
            }
        }

        /// <summary>Names a dialogue node the same on every machine. Uses the authored ID, since UID is minted at runtime.</summary>
        internal static string IdentityOf(DTNode node)
        {
            try
            {
                DialogueTree? tree = node.DLGTree;
                return (tree == null ? "?" : tree.name) + "#" + node.ID;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Finds the loaded dialogue node an <see cref="IdentityOf"/> names, so a teammate's report is held to what that
        /// node authors.
        /// </summary>
        internal static T? FindNode<T>(string identity) where T : DTNode
        {
            int split = identity.LastIndexOf('#');
            if (split <= 0) return null;

            string treeName = identity.Substring(0, split);

            foreach (DialogueTree tree in Resources.FindObjectsOfTypeAll<DialogueTree>())
            {
                if (tree == null || tree.name != treeName) continue;

                // A tree nobody here has talked through yet is still serialized.
                if (tree.allNodes == null || tree.allNodes.Count == 0)
                {
                    tree.SelfDeserialize();
                    tree.DeserializeIfNotDoneYet(true);
                }

                if (tree.allNodes == null) continue;

                for (int index = 0; index < tree.allNodes.Count; index++)
                {
                    Node? candidate = tree.allNodes[index];
                    T? node = candidate == null ? null : candidate.TryCast<T>();
                    if (node != null && IdentityOf(node) == identity) return node;
                }
            }

            return null;
        }
    }
}
