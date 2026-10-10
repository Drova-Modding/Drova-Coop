using Drova_Modding_API;
using DrovaCoopMod.Sync;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.Systems.World.Patches;
using Il2CppDrova.InteractionSystem;
using MelonLoader;

namespace DrovaCoopMod.Systems.World
{
    /// <summary>
    /// Keeps the mine bed's unsaved, locally advanced day cursor in step by re-deriving it from the replicated
    /// globals, forward only (see <see cref="MineDayCursor"/>).
    /// </summary>
    internal sealed class MineDayLoop : ICoopSystem
    {
        // The gate runs on hover, so it would otherwise walk every day's condition each frame.
        private const float ThrottleSeconds = 0.5f;

        private static readonly Dictionary<IntPtr, float> CheckedAt = new();

        string? ICoopSystem.HookSwitch => HookSwitches.MineDay;

        /// <summary>Whether the reseat hook is in place.</summary>
        internal static bool Hooked { get; private set; }

        /// <summary>How often a bed's cursor was examined. For <c>coop_status</c>.</summary>
        internal static int Asked { get; private set; }

        /// <summary>How often a cursor had to be moved on. For <c>coop_status</c>.</summary>
        internal static int Moved { get; private set; }

        /// <summary>Forgets the throttle and the counters.</summary>
        private static void Reset()
        {
            CheckedAt.Clear();
            Asked = 0;
            Moved = 0;
        }

        /// <summary>Applies the patch on the gate, which the interaction always asks first.</summary>
        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            Hooked = Hooking.TryPrefix(harmony, typeof(Interact_Special_MineDayLoop),
                nameof(Interact_Special_MineDayLoop.CheckPreCondition),
                typeof(MineDayLoopPatches), nameof(MineDayLoopPatches.ReseatPrefix));

            if (!Hooked)
            {
                MelonLogger.Warning("[Mine] the bed's day cursor cannot be kept in step; whichever " +
                    "player does not sleep may be told they still have work to do for the rest of the " +
                    "session.");
            }
        }

        internal static void ReseatPrefix(Interact_Special_MineDayLoop __instance)
        {
            if (!CoopSession.HostsWorld
                && !CoopSession.JoinedWorld) return;

            try
            {
                float now = UnityEngine.Time.unscaledTime;

                if (CheckedAt.TryGetValue(__instance.Pointer, out float last)
                    && now - last < ThrottleSeconds) return;

                CheckedAt[__instance.Pointer] = now;
                Asked++;

                Il2CppSystem.Collections.Generic.List<Interact_Special_MineDayLoop.DayLoopContainer> slots =
                    __instance._gBools;

                if (slots == null) return;

                bool[] begun = new bool[slots.Count];

                for (int index = 0; index < slots.Count; index++)
                {
                    Interact_Special_MineDayLoop.DayLoopContainer slot = slots[index];

                    // The authored condition is pure, so it is safe to ask from a prefix.
                    begun[index] = slot != null && slot.FinishedCond != null && slot.FinishedCond.CheckCondition();
                }

                int want = MineDayCursor.Resolve(begun, __instance._startIndex);

                if (want == __instance._startIndex) return;

                __instance._startIndex = want;
                Moved++;

                MelonLogger.Msg("[Mine] a teammate already lived through this day in the mine; this " +
                    $"world's bed was moved on to day {want + 1}.");
            }
            catch (Exception e)
            {
                // Fails open: throwing out of the gate would make the bed unusable.
                MelonLogger.Error("[Mine] reseating the mine's day cursor failed: " + e);
            }
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings) => Reset();
    }
}
