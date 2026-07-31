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
    /// Makes the creatures a spell conjures one creature rather than two.
    ///
    /// **Both machines already build them, and that is the whole starting position.** A cast is replicated:
    /// the far side's copy of the caster plays the same ability from the same prefab, so its conjure runs
    /// there too and spawns its own summons without being told to. What comes out is the same creatures in
    /// every respect a player can see, and in none the session can use - each was instantiated locally, so
    /// each carries a guid minted in its own process and neither can be named to the other.
    ///
    /// Left alone that produces two independent sets. Each fights what its own machine's behavior picks,
    /// each has its own health, and each dies on its own schedule. The version a client sees is worse than
    /// merely different: its own copy swings at creatures the host owns, whose health is write-protected
    /// here, and only a blow struck by the player themselves is ever reported - so a client's summon appears
    /// to fight and lands nothing, while the host's copy of that same summon does the real damage somewhere
    /// else entirely.
    ///
    /// **So the pairing rides on the cast, which is the one thing both machines agree about.** A conjure
    /// spawns from a fixed list in a fixed order, so the *n*th summon of a given player's *s*th cast names
    /// the same creature everywhere. The host claims its copy and says which handle it gave it; the far side
    /// attaches that handle to the body it already has. After that a summon is an ordinary member of the
    /// session - driven, write-protected, its health and death arbitrated - and its local behavior is
    /// switched off, which is what stops the two copies wandering apart.
    ///
    /// **Read from the conjure's own list rather than hooked per spawn.** The behaviour appends every
    /// creature it makes to <c>_spawnedInstances</c>, so that list *is* the ordering the index refers to,
    /// including the delayed conjures that spawn from a timer long after the cast returned. Reconciling from
    /// it needs no second hook and cannot disagree with the game about what order things happened in.
    /// </summary>
    internal static class SummonWatch
    {
        /// <summary>
        /// How many casts are remembered at once.
        ///
        /// One entry per conjure currently alive; the game's own last five seconds after its owner leaves
        /// combat. Well past any real number, and it exists so a mod that conjures in a loop cannot grow
        /// this without bound.
        /// </summary>
        private const int MaxTrackedCasts = 64;

        /// <summary>
        /// How long an unmatched pairing waits for its body.
        ///
        /// A conjure can carry a delay, and the far side's copy runs on its own clock, so the handle
        /// routinely arrives before the creature it names. Ten seconds is far past the delays the game
        /// authors and short enough that a pairing for a cast this machine never ran does not linger.
        /// </summary>
        private const float PatienceSeconds = 10f;

        /// <summary>
        /// One conjure being watched, and the cast it belongs to.
        /// </summary>
        private sealed class Conjure(ABI_Bhvr_Conjure behaviour, PlayerId caster, byte sequence)
        {
            internal ABI_Bhvr_Conjure Behaviour { get; } = behaviour;

            internal PlayerId Caster { get; } = caster;

            internal byte Sequence { get; } = sequence;

            /// <summary>Which indices have already been claimed here, so each is announced once.</summary>
            internal HashSet<int> Handled { get; } = new();

            /// <summary>Seconds since the cast, for forgetting one whose behaviour outlived its batch.</summary>
            internal float Age { get; set; }
        }

        /// <summary>
        /// How long a cast is remembered.
        ///
        /// **Not merely tidiness: the behaviour object does not always go when its batch does.** The ability
        /// clears its own list of instances when it unregisters, and whether the object holding that list is
        /// then destroyed or returned to a pool is the ability system's business rather than something that
        /// can be relied on here. Without an age, an entry whose object is pooled sits in the table forever.
        ///
        /// Far longer than a conjure lives - the game's own end five seconds after their owner leaves combat
        /// - because the only cost of remembering one too long is a dictionary entry, while forgetting one
        /// too early is a summon that never gets paired.
        /// </summary>
        private const float RememberSeconds = 120f;

        /// <summary>One pairing that has arrived and not yet found its body.</summary>
        private struct Waiting
        {
            internal SummonBindMessage Message;

            internal float Waited;
        }

        private static readonly Dictionary<IntPtr, Conjure> _conjures = new();
        private static readonly List<IntPtr> _gone = new();
        private static readonly List<Waiting> _waiting = new();

        // The cast currently being applied on this machine, which is what gives a conjure its name. Set
        // around the call that starts an item's use, by both the local player's path and the puppet's.
        private static bool _inCast;
        private static PlayerId _castCaster;
        private static byte _castSequence;

        private static int _shared;
        private static int _paired;
        private static int _unnamed;
        private static bool _hookFired;

        /// <summary>How many conjured creatures this machine has claimed and named. For status.</summary>
        internal static int Shared => _shared;

        /// <summary>How many this machine attached to a handle somebody else gave. For status.</summary>
        internal static int Paired => _paired;

        /// <summary>
        /// How many conjures ran outside a cast this mod could name, so their creatures stay local. A
        /// creature's own summon is the ordinary case: only a player's cast is replicated.
        /// </summary>
        internal static int Unnamed => _unnamed;

        /// <summary>Whether the conjure hook has ever run. Without it nothing here happens at all.</summary>
        internal static bool HookFired => _hookFired;

        /// <summary>
        /// Applies the patch. Called once at melon init.
        ///
        /// <c>OnAbilityInit</c> is where a conjure decides everything about its batch - how many, from which
        /// prefabs, in what order - and it runs inside the cast that started it, which is the only moment
        /// the cast can still be named. Resolved by signature because the base declares the same method.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            MethodBase? target = HarmonyLib.AccessTools.Method(
                typeof(ABI_Bhvr_Conjure),
                nameof(ABI_Bhvr_Conjure.OnAbilityInit),
                new[] { typeof(ABI_Controller) });

            Hooking.TryPostfix(harmony, target, typeof(SummonWatch), nameof(OnAbilityInitPostfix),
                "ABI_Bhvr_Conjure.OnAbilityInit");
        }

        /// <summary>
        /// Opens the naming of a cast. Called by whichever path is about to start an item's use.
        /// </summary>
        /// <param name="caster">Whose cast it is.</param>
        /// <param name="sequence">Which of that player's casts, as the cast replication numbers them.</param>
        internal static void BeginCast(PlayerId caster, byte sequence)
        {
            _inCast = true;
            _castCaster = caster;
            _castSequence = sequence;
        }

        /// <summary>Closes the naming of a cast. Must be paired with <see cref="BeginCast"/>.</summary>
        internal static void EndCast()
        {
            _inCast = false;
        }

        /// <summary>
        /// Claims what the host conjured and attaches what the host has named. Call once per frame from a
        /// loaded world.
        /// </summary>
        /// <param name="unscaledDeltaTime">Seconds since the last frame.</param>
        internal static void Update(float unscaledDeltaTime)
        {
            if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined)
            {
                Clear();
                return;
            }

            // **Closed here as well as by whoever opened it.** A cast begins and ends inside one call, so a
            // scope still open at the top of a frame is one whose closing was skipped - which is what
            // happens when the game's own item-use path throws under a prefix that already opened it. Left
            // standing, the next creature to conjure anything would be recorded as that player's spell.
            _inCast = false;

            try
            {
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
            _conjures.Clear();
            _waiting.Clear();
            _inCast = false;
        }

        // ------------------------------------------------------------------ noticing

        /// <summary>
        /// Records a conjure against the cast that started it.
        ///
        /// A conjure outside a named cast is a creature's own summon rather than a player's. Only a player's
        /// cast is replicated, so the far side is not running this one at all and there is nothing to pair
        /// with - it is left entirely local, which is what it was before.
        /// </summary>
        private static void OnAbilityInitPostfix(ABI_Bhvr_Conjure __instance)
        {
            try
            {
                _hookFired = true;

                if (__instance == null) return;
                if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined) return;

                if (!_inCast)
                {
                    _unnamed++;
                    return;
                }

                // Refused rather than evicting somebody else's cast, which would unpair summons that are
                // still being fought over. Ages expire entries continuously, so reaching this means casts
                // are arriving faster than that - which is a mod conjuring in a loop, not play.
                if (_conjures.Count >= MaxTrackedCasts && !_conjures.ContainsKey(__instance.Pointer)) return;

                _conjures[__instance.Pointer] = new Conjure(__instance, _castCaster, _castSequence);
            }
            catch (Exception e)
            {
                // Swallowed: this runs inside the game's own ability setup, and an exception let out of here
                // would take the spell with it.
                MelonLogger.Error("[Summon] noticing a conjured creature failed: " + e);
            }
        }

        private static void DropWhatIsGone(float unscaledDeltaTime)
        {
            if (_conjures.Count == 0) return;

            _gone.Clear();

            foreach (KeyValuePair<IntPtr, Conjure> conjure in _conjures)
            {
                conjure.Value.Age += unscaledDeltaTime;

                if (conjure.Value.Behaviour == null || conjure.Value.Age >= RememberSeconds)
                {
                    _gone.Add(conjure.Key);
                }
            }

            foreach (IntPtr pointer in _gone)
            {
                _conjures.Remove(pointer);
            }
        }

        // ------------------------------------------------------------------ host

        private static void ClaimAndAnnounce()
        {
            foreach (KeyValuePair<IntPtr, Conjure> pair in _conjures)
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
                    _shared++;

                    CoopSession.BroadcastSummonBind(conjure.Caster, conjure.Sequence, (byte)index, id);
                }
            }
        }

        // ------------------------------------------------------------------ client

        private static void TakePairings(float unscaledDeltaTime)
        {
            while (CoopSession.TryTakeSummonBind(out SummonBindMessage message))
            {
                _waiting.Add(new Waiting { Message = message });
            }

            if (_waiting.Count == 0) return;

            // Backwards, so removing the one that landed does not skip the next.
            for (int index = _waiting.Count - 1; index >= 0; index--)
            {
                Waiting waiting = _waiting[index];

                if (TryPair(waiting.Message))
                {
                    _waiting.RemoveAt(index);
                    continue;
                }

                waiting.Waited += unscaledDeltaTime;

                // Given up on rather than kept. A pairing whose cast never ran here names a creature this
                // machine does not have and never will, and holding it forever is a list that only grows.
                if (waiting.Waited >= PatienceSeconds)
                {
                    _waiting.RemoveAt(index);
                    continue;
                }

                _waiting[index] = waiting;
            }
        }

        private static bool TryPair(in SummonBindMessage message)
        {
            foreach (KeyValuePair<IntPtr, Conjure> pair in _conjures)
            {
                Conjure conjure = pair.Value;

                if (conjure.Caster != message.Caster || conjure.Sequence != message.CastSequence) continue;

                Il2CppSystem.Collections.Generic.List<GameObject> spawned = conjure.Behaviour._spawnedInstances;
                if (spawned == null) return false;

                // The body has not been made yet. Not a failure: a conjure can carry a delay, and this
                // machine's copy of the cast runs on its own clock.
                if (message.Index >= spawned.Count) return false;

                if (!TryResolve(spawned, message.Index, out Actor body)) return false;

                if (conjure.Handled.Contains(message.Index)) return true;

                conjure.Handled.Add(message.Index);
                _paired++;

                // Bound and driven from here, which also switches its own behavior off. Two copies deciding
                // for themselves is exactly what this is for.
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

            // Claimed from a body that can answer for itself: the guid a claim is keyed by is assigned as
            // the actor initializes, and so is the health everything downstream reads.
            return EnemyRegistry.IsReady(body);
        }
    }
}
