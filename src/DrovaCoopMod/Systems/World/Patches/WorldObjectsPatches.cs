using Il2CppDrova.Saveables;

namespace DrovaCoopMod.Systems.World.Patches
{
    /// <summary>The Harmony entry points of <see cref="WorldObjects"/>. Each forwards to the system.</summary>
    internal static class WorldObjectsPatches
    {
        internal static void BoolInitPostfix(Saveable_GenericBoolState __instance)
            => WorldObjects.BoolInitPostfix(__instance);

        internal static void BoolChangedPostfix(Saveable_GenericBoolState __instance)
            => WorldObjects.BoolChangedPostfix(__instance);

        internal static void FloatInitPostfix(Saveable_GenericFloatState __instance)
            => WorldObjects.FloatInitPostfix(__instance);

        internal static void FloatChangedPostfix(Saveable_GenericFloatState __instance)
            => WorldObjects.FloatChangedPostfix(__instance);

        internal static void ActivationStartPostfix(Saveable_GameObject_ActivationState __instance)
            => WorldObjects.ActivationStartPostfix(__instance);

        internal static void ActivationChangedPostfix(Saveable_GameObject_ActivationState __instance)
            => WorldObjects.ActivationChangedPostfix(__instance);

        internal static void AlrauneStartPostfix(Saveable_Alraune __instance)
            => WorldObjects.AlrauneStartPostfix(__instance);

        internal static void AlrauneChangedPostfix(Saveable_Alraune __instance)
            => WorldObjects.AlrauneChangedPostfix(__instance);
    }
}
