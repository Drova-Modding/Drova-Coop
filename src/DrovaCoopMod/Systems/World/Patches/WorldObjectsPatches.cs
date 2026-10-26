using Il2CppDrova.InteractionSystem;
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

        internal static void DoorStartPostfix(Interact_Bhvr_Door __instance)
            => WorldObjects.DoorStartPostfix(__instance);

        internal static void DoorInteractionPostfix(Interact_Bhvr_Door __instance, InteractArgs args)
            => WorldObjects.DoorInteractionPostfix(__instance, args);

        internal static void DepotStartPostfix(Saveable_Depot __instance)
            => WorldObjects.DepotStartPostfix(__instance);

        internal static void DepotLoadedPostfix(Saveable_Depot __instance)
            => WorldObjects.DepotLoadedPostfix(__instance);

        internal static void LockUnlockedPostfix(Interact_Condition_Locked __instance)
            => WorldObjects.LockUnlockedPostfix(__instance);

        internal static void AlrauneChangedPostfix(Saveable_Alraune __instance)
            => WorldObjects.AlrauneChangedPostfix(__instance);

        internal static void ElevatorAwakePostfix(Saveable_Elevator __instance)
            => WorldObjects.ElevatorAwakePostfix(__instance);

        internal static void ElevatorPanelPostfix(Interact_Bhvr_ElevatorPanel __instance)
            => WorldObjects.ElevatorPanelPostfix(__instance);

        internal static void SpriteAwakePostfix(Saveable_HitBhvrSpriteChange __instance)
            => WorldObjects.SpriteAwakePostfix(__instance);

        internal static void SpriteChangedPostfix(Saveable_HitBhvrSpriteChange __instance)
            => WorldObjects.SpriteChangedPostfix(__instance);
    }
}
