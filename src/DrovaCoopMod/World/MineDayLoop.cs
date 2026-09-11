using Drova_Modding_API;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Il2CppDrova;
using Il2CppDrova.InteractionSystem;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Keeps the mine's bed pointing at the same day in both worlds.
    ///
    /// The bed walks a list of days, and which one it is on lives in a private field that is never
    /// saved, never replicated, and advanced only on the machine whose player actually slept - while
    /// every variable that list reads is an ordinary global that this session replicates in full. So one
    /// rollover is enough to make the two beds disagree. The machine that did not sleep still tests
    /// yesterday's gate, which the crossed variables have since satisfied and moved past, and is told
    /// "I've got work to do" for the rest of the session; or, when the old gate happens to still read
    /// true, it starts yesterday's morning again - which is a scene playing out around a player who has
    /// already lived it, with the camera on an NPC who has nowhere left to walk.
    ///
    /// Re-derived here rather than sent, and that is the cheaper answer as well as the more robust one:
    /// the game's own <c>Awake</c> computes this cursor from the world's variables, those variables
    /// already cross, and so the same computation run again on the other machine gets the same answer
    /// however that machine got there. Nothing goes on the wire and there is nothing to keep in step.
    ///
    /// Forward only. Between a player starting a morning and that scene writing its day flag, their own
    /// world is legitimately a slot ahead of what the variables say; rewinding there would let the same
    /// morning be started twice. See <see cref="MineDayCursor"/>, where that rule is tested.
    /// </summary>
    internal static class MineDayLoop
    {
        /// <summary>
        /// How often one bed is re-derived at most. The gate this hangs off runs on hover, so without a
        /// throttle it is a per-frame walk of every day's condition while a player stands near the bed.
        /// </summary>
        private const float ThrottleSeconds = 0.5f;

        private static readonly Dictionary<IntPtr, float> _checkedAt = new();

        private static bool _hooked;
        private static int _asked;
        private static int _moved;

        /// <summary>Whether the reseat hook is in place.</summary>
        internal static bool Hooked => _hooked;

        /// <summary>
        /// How often a bed's cursor was examined. For <c>coop_status</c>, and it is the number that
        /// matters most: a patch that applied and never fired looks exactly like a fix that worked.
        /// </summary>
        internal static int Asked => _asked;

        /// <summary>How often a cursor had to be moved on. For <c>coop_status</c>.</summary>
        internal static int Moved => _moved;

        /// <summary>Forgets the throttle and the counters.</summary>
        internal static void Reset()
        {
            _checkedAt.Clear();
            _asked = 0;
            _moved = 0;
        }

        /// <summary>
        /// Applies the patch. Called once at melon init.
        ///
        /// On the gate rather than on <c>BeginInteraction</c>, because the interaction calls the gate
        /// first and only begins when it said yes - so the reseat has already happened in the same frame,
        /// and a second patch could only ever land between the two.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            _hooked = Hooking.TryPrefix(harmony, typeof(Interact_Special_MineDayLoop),
                nameof(Interact_Special_MineDayLoop.CheckPreCondition),
                typeof(MineDayLoop), nameof(ReseatPrefix));

            if (!_hooked)
            {
                MelonLogger.Warning("[Mine] the bed's day cursor cannot be kept in step; whichever " +
                    "player does not sleep may be told they still have work to do for the rest of the " +
                    "session.");
            }
        }

        private static void ReseatPrefix(Interact_Special_MineDayLoop __instance)
        {
            if (CoopSession.State != CoopSessionState.Hosting
                && CoopSession.State != CoopSessionState.Joined) return;

            try
            {
                float now = UnityEngine.Time.unscaledTime;

                if (_checkedAt.TryGetValue(__instance.Pointer, out float last)
                    && now - last < ThrottleSeconds) return;

                _checkedAt[__instance.Pointer] = now;
                _asked++;

                Il2CppSystem.Collections.Generic.List<Interact_Special_MineDayLoop.DayLoopContainer> slots =
                    __instance._gBools;

                if (slots == null) return;

                bool[] begun = new bool[slots.Count];

                for (int index = 0; index < slots.Count; index++)
                {
                    Interact_Special_MineDayLoop.DayLoopContainer slot = slots[index];

                    // The authored test, read rather than reinvented. It is pure - it compares global
                    // variables and raises nothing - so it is safe to ask from inside a prefix.
                    begun[index] = slot != null && slot.FinishedCond != null && slot.FinishedCond.CheckCondition();
                }

                int want = MineDayCursor.Resolve(begun, __instance._startIndex);

                if (want == __instance._startIndex) return;

                __instance._startIndex = want;
                _moved++;

                MelonLogger.Msg("[Mine] a teammate already lived through this day in the mine; this " +
                    $"world's bed was moved on to day {want + 1}.");
            }
            catch (Exception e)
            {
                // Fails open on purpose. The worst outcome of not reseating is the desync this exists to
                // prevent; the worst outcome of throwing out of an interaction gate is a bed that cannot
                // be used at all, which is strictly worse.
                MelonLogger.Error("[Mine] reseating the mine's day cursor failed: " + e);
            }
        }
    }
}
