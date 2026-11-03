using System.Diagnostics.CodeAnalysis;
using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Sync;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova.Cutscenes;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;
using UnityEngine.AddressableAssets;
using Object = UnityEngine.Object;

namespace DrovaCoopMod.Systems.World
{
    /// <summary>
    /// Replays shared cutscenes another player started against this machine's world, so they move this player too.
    /// The asset may not be loaded here, so it is resolved from memory, then by addressable guid, then retried.
    /// </summary>
    internal sealed class RemoteCutscene : ICoopSystem
    {
        /// <summary>How long an unresolved cutscene keeps being retried.</summary>
        private const float PatienceSeconds = 20f;

        /// <summary>How often the unresolved are retried.</summary>
        private const float RetrySeconds = 2f;

        private const int MaxPending = 64;

        /// <summary>A name not indexed rescans at most this often, so made-up names cost one scan per interval.</summary>
        private const float RescanSeconds = RetrySeconds;

        private static readonly Queue<CutsceneMessage> IncomingCutscenes = new();
        private static readonly List<Pending> PendingCutscenes = new();
        private static readonly Dictionary<string, CS_CutsceneData> LoadedByName = new(StringComparer.Ordinal);

        private static float _sinceRetry;
        private static float _lastScan = float.NegativeInfinity;
        private static bool _warnedAddressable;

        /// <summary>How many cutscenes were replayed here for somebody else's story. For <c>coop_status</c>.</summary>
        internal static int Replayed { get; private set; }

        private static void Reset()
        {
            PendingCutscenes.Clear();
            Replayed = 0;
            _sinceRetry = 0f;
        }

        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (!CoopSession.SharesWorld)
            {
                Reset();
                return;
            }

            while (TryTakeCutscene(out CutsceneMessage message))
            {
                // An unshared name is a teammate's story port, which GateFollow follows.
                // Its data is often not loaded here, so GateFollow rate-limits instead of checking the name.
                if (!CutsceneSyncAccess.IsShared(message.Name))
                {
                    if (!string.IsNullOrEmpty(message.Name)) GateFollow.ArmForTeammate();
                    continue;
                }

                if (!TryReplay(message) && PendingCutscenes.Count < MaxPending)
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

                if (!TryResolve(message.Name, out CS_CutsceneData? data)) return false;

                Il2Cpp.ICutsceneAction? action = data.GetCutsceneAction(message.ActionId);
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
        private static bool TryResolve(string name, [NotNullWhen(true)] out CS_CutsceneData? data)
        {
            if (LoadedByName.TryGetValue(name, out data) && data != null) return true;

            if (Time.unscaledTime - _lastScan >= RescanSeconds)
            {
                _lastScan = Time.unscaledTime;
                IndexLoaded();

                if (LoadedByName.TryGetValue(name, out data) && data != null) return true;
            }

            data = null;

            if (!CutsceneSyncAccess.TryGetAddressableGuid(name, out string guid)) return false;

            try
            {
                CS_CutsceneData? loaded = new AssetReference(guid)
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

        /// <summary>Files every cutscene in memory by name in one pass.</summary>
        private static void IndexLoaded()
        {
            foreach (Object loaded in Resources.FindObjectsOfTypeAll(
                Il2CppType.Of<CS_CutsceneData>()))
            {
                CS_CutsceneData? candidate = loaded == null ? null : loaded.TryCast<CS_CutsceneData>();
                if (candidate == null) continue;

                string name = candidate.name;
                if (!LoadedByName.TryGetValue(name, out CS_CutsceneData? known) || known == null) LoadedByName[name] = candidate;
            }
        }

        void ICoopSystem.RegisterMessages()
        {
            // Never relayed back to the sender, because replaying a cutscene twice is not a no-op.
            CoopSession.Register<CutsceneMessage>(MessageIds.Cutscene,
                (peer, m) => CoopSession.RelayAndQueue(peer, m, IncomingCutscenes, CoopSession.MaxPendingFromHost, "cutscene starts"),
                worldOnly: true);
        }

        void ICoopSystem.EndSession()
        {
            IncomingCutscenes.Clear();
        }

        private static bool TryTakeCutscene(out CutsceneMessage message)
            => IncomingCutscenes.TryDequeue(out message);

        /// <summary>Reports a shared cutscene starting here. The host replays it and passes it on.</summary>
        internal static void ReportCutscene(in CutsceneMessage message)
            => CoopSession.SendIfLive(message);

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (!loaded) Reset();
        }

        private sealed class Pending
        {
            internal CutsceneMessage Message;

            internal float Waited;
        }
    }
}
