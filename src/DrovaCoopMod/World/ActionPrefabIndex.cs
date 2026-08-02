using Il2CppDrova.ActorActions;
using Il2CppDrova.Pooling;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Names action prefabs across machines, by a hash of the name the game itself pools them under.
    ///
    /// The problem this solves: a creature's attack is an action prefab, and a prefab cannot cross a
    /// wire. What can cross is its asset name, because both installations read the same assets - and the
    /// game keeps a single flat list of every action prefab it will ever play, serialized on the object
    /// pooler and loaded whole for the whole session on every machine. <c>ActorActionPooling</c> keys its
    /// pool by <c>name</c> and logs an error on a duplicate, so the name is already the game's own idea
    /// of an action's identity; this file only makes it fit in four bytes.
    ///
    /// Built lazily, because the pooler is a game handler and game handlers do not exist until a world
    /// is up. Never trusted blindly: a hash that resolves to nothing is skipped and said once, because
    /// the honest causes - a version skew between the two machines, an action outside the database - both
    /// degrade to a creature whose swing does not show, not to anything worse.
    /// </summary>
    internal static class ActionPrefabIndex
    {
        private static readonly Dictionary<uint, AA_ABase> _byHash = new();
        private static readonly HashSet<uint> _reportedMisses = new();
        private static bool _built;

        /// <summary>
        /// FNV-1a, 32 bits, over the exact raw name. The same function at both ends is the whole
        /// contract, which is why it is written out here rather than borrowed from the runtime -
        /// <c>string.GetHashCode</c> is randomized per process by design.
        /// </summary>
        /// <param name="name">The prefab asset's name, untrimmed.</param>
        internal static uint Hash(string name)
        {
            uint hash = 2166136261;

            foreach (char c in name)
            {
                hash ^= c;
                hash *= 16777619;
            }

            return hash;
        }

        /// <summary>
        /// Finds the action prefab a hash names on this machine.
        /// </summary>
        /// <param name="hash">The name hash, as sent.</param>
        /// <param name="prefab">The prefab asset.</param>
        /// <returns>False when the database is not up yet or no prefab answers to the hash.</returns>
        internal static bool TryResolve(uint hash, out AA_ABase prefab)
        {
            prefab = null!;

            if (hash == 0) return false;
            if (!_built && !TryBuild()) return false;

            if (_byHash.TryGetValue(hash, out AA_ABase? found) && found != null)
            {
                prefab = found;
                return true;
            }

            // Once per hash rather than per packet, because an unresolvable action arrives thirty times
            // a second for as long as the swing lasts.
            if (_reportedMisses.Add(hash))
            {
                MelonLogger.Warning($"[Enemy] the host played an action this game cannot find " +
                    $"(hash {hash:x8}); that swing will not be shown here. Game versions may differ.");
            }

            return false;
        }

        /// <summary>
        /// Forgets the index. Called when the world goes away, because the entries are proxies over
        /// assets a world teardown may reload - a stale proxy answers <c>null</c> checks correctly but is
        /// not worth keeping when rebuilding costs one walk over a thousand entries.
        /// </summary>
        internal static void Clear()
        {
            _byHash.Clear();
            _reportedMisses.Clear();
            _built = false;
        }

        /// <summary>
        /// Reads the game's own action database into the index.
        ///
        /// Guarded top to bottom because the pooler hangs off the game manager singleton, and asking for
        /// it before the manager exists throws rather than returning false. Failing here is ordinary -
        /// the caller retries on the next resolve, and a world that is far enough along to be applying
        /// enemy snapshots has long since built its handlers.
        /// </summary>
        private static bool TryBuild()
        {
            try
            {
                if (!Il2CppDrova.ObjectPoolerGameHandler.TryGet(out Il2CppDrova.ObjectPoolerGameHandler handler)
                    || handler == null)
                {
                    return false;
                }

                ActorActionPooling pooling = handler.ActorActionPooling;
                if (pooling == null) return false;

                Il2CppSystem.Collections.Generic.List<AA_ABase> database = pooling._aaDatabase;
                if (database == null || database.Count == 0) return false;

                for (int index = 0; index < database.Count; index++)
                {
                    AA_ABase prefab = database[index];
                    if (prefab == null) continue;

                    // The interop marshals a string per read, so the name is read once here and never
                    // again - resolves afterwards are a dictionary lookup on an integer.
                    uint hash = Hash(prefab.name);

                    // First wins, said out loud. The pool itself assumes names are unique and drops
                    // duplicates the same way, so agreeing with it keeps both machines resolving a
                    // collision - hash or name - to the same prefab.
                    if (!_byHash.TryAdd(hash, prefab))
                    {
                        MelonLogger.Warning($"[Enemy] two action prefabs share hash {hash:x8} " +
                            $"('{prefab.name}' lost); its attacks will show as the winner's.");
                    }
                }

                _built = true;

                MelonLogger.Msg($"[Enemy] indexed {_byHash.Count} action prefabs for replicating " +
                    "creature attacks.");

                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] indexing the game's action prefabs failed: " + e);
                return false;
            }
        }
    }
}
