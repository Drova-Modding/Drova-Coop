using Il2CppDrova.GlobalVarSystem;
using Il2CppDrova.QuestSystem;

namespace DrovaCoopMod.Systems.World.Patches
{
    /// <summary>The Harmony entry points of <see cref="GVarWatch"/>. Each forwards to the system.</summary>
    internal static class GVarWatchPatches
    {
        internal static void BoolPrefix(AGVar<bool> __instance, bool t, out GVarWatch.Stamp __state)
            => GVarWatch.BoolPrefix(__instance, t, out __state);

        internal static void BoolPostfix(AGVar<bool> __instance, GVarWatch.Stamp __state)
            => GVarWatch.BoolPostfix(__instance, __state);

        internal static void IntPrefix(AGVar<int> __instance, int t, out GVarWatch.Stamp __state)
            => GVarWatch.IntPrefix(__instance, t, out __state);

        internal static void IntPostfix(AGVar<int> __instance, GVarWatch.Stamp __state)
            => GVarWatch.IntPostfix(__instance, __state);

        internal static void FloatPrefix(AGVar<float> __instance, float t, out GVarWatch.Stamp __state)
            => GVarWatch.FloatPrefix(__instance, t, out __state);

        internal static void FloatPostfix(AGVar<float> __instance, GVarWatch.Stamp __state)
            => GVarWatch.FloatPostfix(__instance, __state);

        internal static void QuestPrefix(AGVar<QuestState> __instance, QuestState t, out GVarWatch.Stamp __state)
            => GVarWatch.QuestPrefix(__instance, t, out __state);

        internal static void QuestPostfix(AGVar<QuestState> __instance, GVarWatch.Stamp __state)
            => GVarWatch.QuestPostfix(__instance, __state);
    }
}
