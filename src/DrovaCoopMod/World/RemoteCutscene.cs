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
    /// Replays shared cutscenes another player started, against this machine's own world.
    ///
    /// The far half of <see cref="CutsceneWatch"/>. The actions resolve their entities here, so "the
    /// player" is this machine's player: a trip that moved the initiator moves this player too, which
    /// is the point of sharing.
    ///
    /// Resolving the data is the hard half. A cutscene asset rides in with the dialogue graph that
    /// uses it, so this machine may not hold it. Three routes, cheapest first: the assets already in
    /// memory, an addressable load by the declared guid, and a retry window for one still streaming
    /// in. A cutscene that cannot be resolved is given up on out loud, because a player silently left
    /// behind is the bug this exists to fix.
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

        private static readonly List<Pending> _pending = new();

        private static float _sinceRetry;
        private static int _replayed;
        private static bool _warnedAddressable;

        /// <summary>How many cutscenes were replayed here for somebody else's story. For <c>coop_status</c>.</summary>
        internal static int Replayed => _replayed;

        /// <summary>Forgets everything. Called when a session ends.</summary>
        internal static void Reset()
        {
            _pending.Clear();
            _replayed = 0;
            _sinceRetry = 0f;
        }

        /// <summary>
        /// Replays everything that has arrived. Call once per frame from a loaded world.
        /// </summary>
        /// <param name="unscaledDeltaTime">Seconds since the last frame.</param>
        internal static void Update(float unscaledDeltaTime)
        {
            if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined)
            {
                Reset();
                return;
            }

            while (CoopSession.TryTakeCutscene(out CutsceneMessage message))
            {
                if (!TryReplay(message))
                {
                    _pending.Add(new Pending { Message = message });
                }
            }

            if (_pending.Count == 0) return;

            _sinceRetry += unscaledDeltaTime;
            if (_sinceRetry < RetrySeconds) return;

            float waited = _sinceRetry;
            _sinceRetry = 0f;

            for (int index = _pending.Count - 1; index >= 0; index--)
            {
                Pending pending = _pending[index];
                pending.Waited += waited;

                if (TryReplay(pending.Message))
                {
                    _pending.RemoveAt(index);
                    continue;
                }

                if (pending.Waited < PatienceSeconds) continue;

                _pending.RemoveAt(index);

                MelonLogger.Warning($"[Cutscene] '{pending.Message.Name}' could not be resolved here, so " +
                    "this player stays behind on that story transition. The asset was neither in memory " +
                    "nor loadable by its declared guid.");
            }
        }

        private static bool TryReplay(in CutsceneMessage message)
        {
            try
            {
                // Nothing to resolve and nothing to wait for. Dropped as handled.
                if (string.IsNullOrEmpty(message.Name)) return true;

                if (!TryResolve(message.Name, out CS_CutsceneData data)) return false;

                Il2Cpp.ICutsceneAction action = data.GetCutsceneAction(message.ActionId);
                if (action == null)
                {
                    // The asset resolved and the action does not exist on it, which retrying cannot
                    // change. Counted as handled so it is not warned about as a missing asset.
                    MelonLogger.Warning($"[Cutscene] '{message.Name}' has no action {message.ActionId}; " +
                        "the versions of this cutscene differ between machines.");
                    return true;
                }

                // The runner has to be a body the world owns, and the local player is the one this
                // cutscene is about to move.
                if (!PlayerAccess.TryGetPlayer(out Il2CppDrova.Actor player) || player == null) return false;

                using (RemoteApply<CutsceneScope>.Enter())
                {
                    action.StartAction();
                    player.StartCoroutine(action.DoAction());
                }

                _replayed++;

                MelonLogger.Msg($"[Cutscene] replaying '{message.Name}' action {message.ActionId} because " +
                    "a teammate's story moved, and this player rides along.");

                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Cutscene] replaying a shared cutscene failed: " + e);

                // Handled, not retried. Whatever threw will throw again, and a cutscene replayed late
                // out of a retry loop is worse than one skipped.
                return true;
            }
        }

        /// <summary>
        /// Finds the cutscene data by name, from memory first and by addressable guid second.
        /// </summary>
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
                // Once, not per retry: the likeliest cause is the closed generic having no AOT body,
                // and that does not change between attempts.
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
