using Il2CppDrova.GlobalVarSystem;

namespace DrovaCoopMod.Systems.World.Patches
{
    /// <summary>The Harmony entry points of <see cref="GVarDerived"/>. Each forwards to the system.</summary>
    internal static class GVarDerivedPatches
    {
        internal static bool OperatePrefix(GInt __instance, GInt.Operator op, GVarContext context)
            => GVarDerived.OperatePrefix(__instance, op, context);

        internal static void OperatePostfix()
            => GVarDerived.OperatePostfix();

        internal static bool BoolOperatePrefix(GBool.Operator o)
            => GVarDerived.BoolOperatePrefix(o);
    }
}
