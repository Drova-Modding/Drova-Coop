using Drova_Modding_API;
using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Sync;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.Systems.World.Patches;
using Il2CppDrova.Cutscenes;
using MelonLoader;

namespace DrovaCoopMod.Systems.World
{
    /// <summary>
    /// Tells the session when a shared cutscene starts here, from <c>CS_CutsceneActionNode.OnExecute</c>
    /// since dialogue nodes, not gvars, start them. Only <see cref="CutsceneSyncAccess"/> declarations cross.
    /// </summary>
    internal sealed class CutsceneWatch : ICoopSystem
    {
        /// <summary>The last cutscene a dialogue started here, and its action.</summary>
        internal static string LastName { get; private set; } = string.Empty;

        /// <inheritdoc cref="LastName"/>
        internal static int LastActionId { get; private set; }

        /// <summary>Cutscene starts told to the session.</summary>
        internal static int Shared { get; private set; }

        /// <summary>When a cutscene last teleported this player (unscaled seconds).</summary>
        internal static float PlayerPortedAt { get; private set; } = float.NegativeInfinity;

        /// <param name="delay">Seconds until the port lands, like a fade-out played first.</param>
        internal static void NotePlayerPorted(float delay = 0f) => PlayerPortedAt = UnityEngine.Time.unscaledTime + delay;

        private static void Reset()
        {
            Shared = 0;
        }

        /// <summary>Applies the patch and declares the vanilla story cutscenes.</summary>
        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            // Declared with its addressable guid, since the asset loads with Jero's dialogue graph the other player may lack.
            CutsceneSyncAccess.MarkShared("CutsceneData_MoveToRedTowerWithJero",
                "49565bc58508b364092d97d936094afc");

            Hooking.TryPostfix(harmony, typeof(CS_CutsceneActionNode), "OnExecute",
                typeof(CutsceneWatchPatches), nameof(CutsceneWatchPatches.OnExecutePostfix));
        }

        internal static void OnExecutePostfix(CS_CutsceneActionNode __instance)
        {
            try
            {
                if (!CoopSession.SharesWorld) return;
                if (RemoteApply<CutsceneScope>.InProgress) return;

                CS_CutsceneData data = __instance.cutscene;
                if (data == null) return;

                string name = data.name;

                // Every cutscene, shared or not, so the follow watch knows which one teleported this player.
                LastName = name;
                LastActionId = __instance.cutsceneActionID;

                if (!CutsceneSyncAccess.IsShared(name)) return;

                Shared++;

                MelonLogger.Msg($"[Cutscene] '{name}' action {__instance.cutsceneActionID} started here " +
                    "and was told to the session.");

                RemoteCutscene.ReportCutscene(new CutsceneMessage
                {
                    Name = name,
                    ActionId = __instance.cutsceneActionID,
                });
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Cutscene] watching a cutscene start failed: " + e);
            }
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (!loaded) Reset();
        }
    }
}
