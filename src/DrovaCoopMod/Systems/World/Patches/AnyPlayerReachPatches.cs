using Il2CppDrova.GlobalVarSystem;
using Il2CppDrova.HitSystem;
using Il2CppDrova.Routine;
using UnityEngine;

namespace DrovaCoopMod.Systems.World.Patches
{
    /// <summary>The Harmony entry points of <see cref="AnyPlayerReach"/>. Each forwards to the system.</summary>
    internal static class AnyPlayerReachPatches
    {
        internal static bool FollowPositionPrefix(FollowActorPoint __instance, ref Vector2 __result)
            => AnyPlayerReach.FollowPositionPrefix(__instance, ref __result);

        internal static void WaitRangePostfix(WaitRangeEntityRoutineCommand __instance, RoutineCommandArgs args, ref bool __result)
            => AnyPlayerReach.WaitRangePostfix(__instance, args, ref __result);

        internal static bool InRangePrefix(IsEntityInRangeCommand __instance, RoutineCommandArgs args, ref bool __result)
            => AnyPlayerReach.InRangePrefix(__instance, args, ref __result);

        internal static bool PlayerAwayPrefix(GlobalObserverBhvr_PlayerAway __instance)
            => AnyPlayerReach.PlayerAwayPrefix(__instance);

        internal static bool SetGVarsExitPrefix(HitReceiveBhvr_SetGVars __instance, HitTriggerArgs args)
            => AnyPlayerReach.SetGVarsExitPrefix(__instance, args);

        internal static bool SetGVarsEnterPrefix(HitReceiveBhvr_SetGVars __instance, HitTriggerArgs args)
            => AnyPlayerReach.SetGVarsEnterPrefix(__instance, args);

        internal static bool HitOncePrefix(HitFilter_HitOnce_Save __instance)
            => AnyPlayerReach.HitOncePrefix(__instance);

        internal static void PlayerInRangePostfix(Il2CppDrova.AI.Graphs.IsPlayerInRangeCondition __instance, ref bool __result)
            => AnyPlayerReach.PlayerInRangePostfix(__instance, ref __result);
    }
}
