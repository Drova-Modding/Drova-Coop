using Drova_Modding_API;
using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Il2CppDrova.Cutscenes;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Watches shared cutscenes starting here, and tells the session.
    ///
    /// <c>CS_CutsceneActionNode.OnExecute</c> is the funnel: a story cutscene is started by a dialogue
    /// graph's cutscene node, not by a variable, which is why the gvar journey-sharing cannot carry
    /// one. Only cutscenes declared through <see cref="CutsceneSyncAccess"/> cross. The rest are
    /// dialogue staging, and staging belongs to the player in the conversation.
    /// </summary>
    internal static class CutsceneWatch
    {
        /// <summary>How many cutscene starts were told to the session. For <c>coop_status</c>.</summary>
        internal static int Shared { get; private set; }

        /// <summary>Forgets the count. Called when a session ends.</summary>
        internal static void Reset()
        {
            Shared = 0;
        }

        /// <summary>
        /// Applies the patch and declares the vanilla story cutscenes. Called once at melon init.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            // The trip to the Red Tower with Jero. Declared with its addressable guid, because the
            // asset rides in with Jero's dialogue graph and the other player may hold nothing to
            // replay without a load of their own. Guid read from the authored asset.
            CutsceneSyncAccess.MarkShared("CutsceneData_MoveToRedTowerWithJero",
                "49565bc58508b364092d97d936094afc");

            Hooking.TryPostfix(harmony, typeof(CS_CutsceneActionNode), "OnExecute",
                typeof(CutsceneWatch), nameof(OnExecutePostfix));
        }

        private static void OnExecutePostfix(CS_CutsceneActionNode __instance)
        {
            try
            {
                if (!CoopSession.IsLive) return;
                if (RemoteApply<CutsceneScope>.InProgress) return;

                CS_CutsceneData data = __instance.cutscene;
                if (data == null) return;

                string name = data.name;
                if (!CutsceneSyncAccess.IsShared(name)) return;

                Shared++;

                MelonLogger.Msg($"[Cutscene] '{name}' action {__instance.cutsceneActionID} started here " +
                    "and was told to the session.");

                CoopSession.ReportCutscene(new CutsceneMessage
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
    }
}
