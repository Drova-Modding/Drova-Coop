using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Il2CppDrova.Cutscenes;
using MelonLoader;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Replays shared cutscenes another player started against this machine's world, so they move this player too.
    /// The asset may not be loaded here, so it is resolved from memory, then by addressable guid, then retried.
    /// </summary>
    internal static class RemoteCutscene
    {
        /// <summary>How long an unresolved cutscene keeps being retried.</summary>
        private const float PatienceSeconds = 20f;

        /// <summary>How often the unresolved are retried.</summary>
        private const float RetrySeconds = 2f;

        private sealed class Pending
        {
            internal CutsceneMessage Message;

            internal float Waited;
        }

        private static readonly List<Pending> PendingCutscenes = new();

        private static float _sinceRetry;
        private static bool _warnedAddressable;

        /// <summary>How many cutscenes were replayed here for somebody else's story. For <c>coop_status</c>.</summary>
        internal static int Replayed { get; private set; }

        /// <summary>Forgets everything. Called when a session ends.</summary>
        internal static void Reset()
        {
            PendingCutscenes.Clear();
            Replayed = 0;
            _sinceRetry = 0f;
        }

        /// <summary>Replays everything that has arrived. Call once per frame from a loaded world.</summary>
        internal static void Update(float unscaledDeltaTime)
        {
            if (!CoopSession.IsLive)
            {
                Reset();
                return;
            }

            while (CoopSession.TryTakeCutscene(out CutsceneMessage message))
            {
                // A teammate's own story cutscene carried them far. GateFollow brings this player along.
                if (!CutsceneSyncAccess.IsShared(message.Name))
                {
                    GateFollow.Arm(GateFollow.StoryPortSeconds);
                    continue;
                }

                if (!TryReplay(message))
                {
                    PendingCutscenes.Add(new Pending { Message = message });
                }
            }

            if (PendingCutscenes.Count == 0) return;

            _sinceRetry += unscaledDeltaTime;
            if (_sinceRetry < RetrySeconds) return;

            float waited = _sinceRetry;
            _sinceRetry = 0f;

            for (int index = PendingCutscenes.Count - 1; index >= 0; index--)
            {
                Pending pending = PendingCutscenes[index];
                pending.Waited += waited;

                if (TryReplay(pending.Message))
                {
                    PendingCutscenes.RemoveAt(index);
                    continue;
                }

                if (pending.Waited < PatienceSeconds) continue;

                PendingCutscenes.RemoveAt(index);

                MelonLogger.Warning($"[Cutscene] '{pending.Message.Name}' could not be resolved here, so " +
                    "this player stays behind on that story transition. The asset was neither in memory " +
                    "nor loadable by its declared guid.");
            }
        }

        private static bool TryReplay(in CutsceneMessage message)
        {
            try
            {
                if (string.IsNullOrEmpty(message.Name)) return true;

                if (!TryResolve(message.Name, out CS_CutsceneData data)) return false;

                Il2Cpp.ICutsceneAction action = data.GetCutsceneAction(message.ActionId);
                if (action == null)
                {
                    // The action does not exist on this asset, which retrying cannot change.
                    MelonLogger.Warning($"[Cutscene] '{message.Name}' has no action {message.ActionId}; " +
                        "the versions of this cutscene differ between machines.");
                    return true;
                }

                // The local player is the one this cutscene moves.
                if (!PlayerAccess.TryGetPlayer(out Il2CppDrova.Actor player)) return false;

                using (RemoteApply<CutsceneScope>.Enter())
                {
                    action.StartAction();
                    player.StartCoroutine(action.DoAction());
                }

                Replayed++;

                MelonLogger.Msg($"[Cutscene] replaying '{message.Name}' action {message.ActionId} because " +
                    "a teammate's story moved, and this player rides along.");

                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Cutscene] replaying a shared cutscene failed: " + e);

                // Not retried. Whatever threw will throw again.
                return true;
            }
        }

        /// <summary>Finds the cutscene data by name, from memory first and by addressable guid second.</summary>
        private static bool TryResolve(string name, out CS_CutsceneData data)
        {
            data = null!;

            foreach (UnityEngine.Object loaded in Resources.FindObjectsOfTypeAll(
                Il2CppInterop.Runtime.Il2CppType.Of<CS_CutsceneData>()))
            {
                if (loaded == null || loaded.name != name) continue;

                CS_CutsceneData? candidate = loaded.TryCast<CS_CutsceneData>();
                if (candidate == null) continue;

                data = candidate;
                return true;
            }

            if (!CutsceneSyncAccess.TryGetAddressableGuid(name, out string guid)) return false;

            try
            {
                CS_CutsceneData loaded = new AssetReference(guid)
                    .LoadAssetAsync<CS_CutsceneData>().WaitForCompletion();

                if (loaded == null) return false;

                data = loaded;
                return true;
            }
            catch (Exception e)
            {
                // Once, not per retry. The likely cause is the closed generic having no AOT body.
                if (!_warnedAddressable)
                {
                    _warnedAddressable = true;
                    MelonLogger.Warning("[Cutscene] loading cutscene data by addressable guid failed: " + e.Message);
                }

                return false;
            }
        }
    }
}
