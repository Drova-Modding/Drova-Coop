using System.Diagnostics.CodeAnalysis;
using Il2CppDrova.ActorActions;
using Il2CppDrova.Pooling;
using MelonLoader;

namespace DrovaCoopMod.Systems.Players
{
    /// <summary>
    /// Names action prefabs across machines by a hash of the name ActorActionPooling keys its pool by.
    /// Built lazily once the pooler exists. A hash that resolves to nothing is skipped and logged once.
    /// </summary>
    internal sealed class ActionPrefabIndex : ICoopSystem
    {
        private static readonly Dictionary<uint, AA_ABase> ByHash = new();
        private static readonly HashSet<uint> ReportedMisses = new();
        private static bool _built;

        /// <summary>FNV-1a over the raw name, because <c>string.GetHashCode</c> is randomized per process.</summary>
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

        /// <summary>Finds the action prefab a hash names on this machine.</summary>
        /// <returns>False when the database is not up yet or no prefab answers to the hash.</returns>
        internal static bool TryResolve(uint hash, [NotNullWhen(true)] out AA_ABase? prefab)
        {
            prefab = null;

            if (hash == 0) return false;
            if (!_built && !TryBuild()) return false;

            if (ByHash.TryGetValue(hash, out AA_ABase? found) && found != null)
            {
                prefab = found;
                return true;
            }

            // Once per hash, since an unresolvable action arrives every snapshot while the swing lasts.
            if (ReportedMisses.Add(hash))
            {
                MelonLogger.Warning($"[Enemy] the host played an action this game cannot find " +
                    $"(hash {hash:x8}); that swing will not be shown here. Game versions may differ.");
            }

            return false;
        }

        /// <summary>Forgets the index. Called when the world goes away, since a teardown may reload the assets.</summary>
        private static void Clear()
        {
            ByHash.Clear();
            ReportedMisses.Clear();
            _built = false;
        }

        /// <summary>Reads the game's action database into the index. Fails until the game manager exists, the caller retries.</summary>
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

                    uint hash = Hash(prefab.name);

                    // First wins, as in the pool itself, so both machines resolve a collision the same way.
                    if (!ByHash.TryAdd(hash, prefab))
                    {
                        MelonLogger.Warning($"[Enemy] two action prefabs share hash {hash:x8} " +
                            $"('{prefab.name}' lost); its attacks will show as the winner's.");
                    }
                }

                _built = true;

                MelonLogger.Msg($"[Enemy] indexed {ByHash.Count} action prefabs for replicating " +
                    "creature attacks.");

                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] indexing the game's action prefabs failed: " + e);
                return false;
            }
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            // Holds the ended world's pooled prefabs. Rebuilt lazily.
            if (!loaded) Clear();
        }
    }
}
