using Drova_Modding_API;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.ABI;
using MelonLoader;
using System.Reflection;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Pairs the creatures a replicated conjure spawns on both machines into one shared creature.
    /// A summon is named by caster, cast sequence and its index in the conjure's <c>_spawnedInstances</c>.
    /// </summary>
    internal static class SummonWatch
    {
        /// <summary>Caps the table so a mod conjuring in a loop cannot grow it without bound.</summary>
        private const int MaxTrackedCasts = 64;

        /// <summary>A conjure can carry a delay, so the handle often arrives before the body it names.</summary>
        private const float PatienceSeconds = 10f;

        /// <summary>The behavior object may be pooled rather than destroyed, so entries also expire by age.</summary>
        private const float RememberSeconds = 120f;

        /// <summary>
        /// A conjure spawns frames after the cast, from <c>AA_Atk_TriggerBase.CheckTriggersInPhase</c> after the
        /// wind-up, so a cast names its summons for a window rather than a scope.
        /// </summary>
        private const float CastNamedSeconds = 5f;

        private sealed class Conjure(ABI_Bhvr_Conjure behaviour, PlayerId caster, byte sequence)
        {
            internal ABI_Bhvr_Conjure Behaviour { get; } = behaviour;

            internal PlayerId Caster { get; } = caster;

            internal byte Sequence { get; } = sequence;

            /// <summary>Indices already claimed here, so each is announced once.</summary>
            internal HashSet<int> Handled { get; } = new();

            internal float Age { get; set; }
        }

        private struct Waiting
        {
            internal SummonBindMessage Message;

            internal float Waited;
        }

        /// <summary>The last cast one body made, which is what gives a conjure its name.</summary>
        private sealed class Cast(PlayerId caster, byte sequence)
        {
            internal PlayerId Caster { get; } = caster;

            internal byte Sequence { get; } = sequence;

            internal float Age { get; set; }
        }

        private static readonly Dictionary<IntPtr, Conjure> Conjures = new();
        private static readonly List<Waiting> WaitingPairings = [];

        // Keyed by body so a creature's own summon stays unnamed instead of going to whoever cast last.
        private static readonly Dictionary<IntPtr, Cast> Casts = new();

        /// <summary>How many conjured creatures this machine has claimed and named. For status.</summary>
        internal static int Shared { get; private set; }

        /// <summary>How many this machine attached to a handle somebody else gave. For status.</summary>
        internal static int Paired { get; private set; }

        /// <summary>How many conjures ran outside a player's cast, so their creatures stay local.</summary>
        internal static int Unnamed { get; private set; }

        /// <summary>Whether the conjure hook has ever run.</summary>
        internal static bool HookFired { get; private set; }

        /// <summary>
        /// Hooks <c>OnAbilityInit</c>, the last point that says which body a conjure batch belongs to.
        /// Resolved by signature because the base declares the same method.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            MethodBase? target = HarmonyLib.AccessTools.Method(
                typeof(ABI_Bhvr_Conjure),
                nameof(ABI_Bhvr_Conjure.OnAbilityInit),
                [typeof(ABI_Controller)]);

            Hooking.TryPostfix(harmony, target, typeof(SummonWatch), nameof(OnAbilityInitPostfix),
                "ABI_Bhvr_Conjure.OnAbilityInit");
        }

        /// <summary>Names a cast, so anything it conjures over the next few seconds can be attributed to it.</summary>
        internal static void BeginCast(PlayerId caster, byte sequence, Actor body)
        {
            if (body == null) return;

            Casts[body.Pointer] = new Cast(caster, sequence);
        }

        /// <summary>Claims what the host conjured and attaches what the host has named. Call once per frame.</summary>
        internal static void Update(float unscaledDeltaTime)
        {
            if (!CoopSession.IsLive)
            {
                Clear();
                return;
            }

            try
            {
                ExpireCasts(unscaledDeltaTime);
                DropWhatIsGone(unscaledDeltaTime);

                if (CoopSession.State == CoopSessionState.Hosting)
                {
                    ClaimAndAnnounce();
                    return;
                }

                TakePairings(unscaledDeltaTime);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Summon] keeping conjured creatures in step failed: " + e);
            }
        }

        /// <summary>Forgets every cast. Called when a session ends or the world goes away.</summary>
        internal static void Clear()
        {
            Conjures.Clear();
            WaitingPairings.Clear();
            Casts.Clear();
        }

        private static void ExpireCasts(float unscaledDeltaTime)
        {
            if (Casts.Count == 0) return;

            foreach (KeyValuePair<IntPtr, Cast> cast in Casts)
            {
                cast.Value.Age += unscaledDeltaTime;

                if (cast.Value.Age >= CastNamedSeconds)
                {
                    Casts.Remove(cast.Key);
                }
            }
        }

        /// <summary>Records a conjure against its body's recent cast. Without one it is a creature's own summon and stays local.</summary>
        private static void OnAbilityInitPostfix(ABI_Bhvr_Conjure __instance)
        {
            try
            {
                HookFired = true;

                if (__instance == null) return;
                if (!CoopSession.IsLive) return;

                // OnAbilityInit assigns _owner before it spawns anything.
                Actor owner = __instance._owner;

                if (owner == null || !Casts.TryGetValue(owner.Pointer, out Cast? cast))
                {
                    Unnamed++;
                    return;
                }

                // Refused rather than evicting a cast whose summons are still being fought over.
                if (Conjures.Count >= MaxTrackedCasts && !Conjures.ContainsKey(__instance.Pointer)) return;

                Conjures[__instance.Pointer] = new Conjure(__instance, cast.Caster, cast.Sequence);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Summon] noticing a conjured creature failed: " + e);
            }
        }

        private static void DropWhatIsGone(float unscaledDeltaTime)
        {
            if (Conjures.Count == 0) return;

            foreach (KeyValuePair<IntPtr, Conjure> conjure in Conjures)
            {
                conjure.Value.Age += unscaledDeltaTime;

                if (conjure.Value.Behaviour == null || conjure.Value.Age >= RememberSeconds)
                {
                    Conjures.Remove(conjure.Key);
                }
            }
        }

        private static void ClaimAndAnnounce()
        {
            foreach (KeyValuePair<IntPtr, Conjure> pair in Conjures)
            {
                Conjure conjure = pair.Value;

                Il2CppSystem.Collections.Generic.List<GameObject> spawned = conjure.Behaviour._spawnedInstances;
                if (spawned == null) continue;

                int count = spawned.Count;
                if (count > SummonBindMessage.MaxIndex) count = SummonBindMessage.MaxIndex;

                for (int index = 0; index < count; index++)
                {
                    if (conjure.Handled.Contains(index)) continue;

                    if (!TryResolve(spawned, index, out Actor body)) continue;

                    if (!EnemyRegistry.TryClaimSpawned(body, out EnemyNetId id)) continue;

                    conjure.Handled.Add(index);
                    Shared++;

                    CoopSession.BroadcastSummonBind(conjure.Caster, conjure.Sequence, (byte)index, id);
                }
            }
        }

        private static void TakePairings(float unscaledDeltaTime)
        {
            while (CoopSession.TryTakeSummonBind(out SummonBindMessage message))
            {
                WaitingPairings.Add(new Waiting { Message = message });
            }

            if (WaitingPairings.Count == 0) return;

            for (int index = WaitingPairings.Count - 1; index >= 0; index--)
            {
                Waiting waiting = WaitingPairings[index];

                if (TryPair(waiting.Message))
                {
                    WaitingPairings.RemoveAt(index);
                    continue;
                }

                waiting.Waited += unscaledDeltaTime;

                // A pairing whose cast never ran here names a creature this machine will never have.
                if (waiting.Waited >= PatienceSeconds)
                {
                    WaitingPairings.RemoveAt(index);
                    continue;
                }

                WaitingPairings[index] = waiting;
            }
        }

        private static bool TryPair(in SummonBindMessage message)
        {
            foreach (KeyValuePair<IntPtr, Conjure> pair in Conjures)
            {
                Conjure conjure = pair.Value;

                if (conjure.Caster != message.Caster || conjure.Sequence != message.CastSequence) continue;

                Il2CppSystem.Collections.Generic.List<GameObject> spawned = conjure.Behaviour._spawnedInstances;
                if (spawned == null) return false;

                // Not spawned yet: the conjure may carry a delay.
                if (message.Index >= spawned.Count) return false;

                if (!TryResolve(spawned, message.Index, out Actor body)) return false;

                if (!conjure.Handled.Add(message.Index)) return true;

                Paired++;

                // Binding also switches its local behavior off, so the two copies stop deciding for themselves.
                EnemyRegistry.BindSpawnedBody(message.NetId, body);

                return true;
            }

            return false;
        }

        private static bool TryResolve(
            Il2CppSystem.Collections.Generic.List<GameObject> spawned, int index, out Actor body)
        {
            body = null!;

            GameObject spawnedObject = spawned[index];
            if (spawnedObject == null) return false;

            body = spawnedObject.GetComponent<Actor>();

            // The guid a claim is keyed by is assigned as the actor initializes.
            return EnemyRegistry.IsReady(body);
        }
    }
}
