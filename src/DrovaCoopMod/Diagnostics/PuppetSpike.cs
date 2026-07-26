#if DEBUG
using Drova_Modding_API.Access;
using DrovaCoopMod.World;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Diagnostics
{
    /// <summary>
    /// M1's puppet spike: spawns one remote-player body next to the local player, with no session
    /// involved, so the clone-and-strip recipe can be exercised on its own.
    ///
    /// It answered its original question - a second player body renders, is inert, and does not corrupt
    /// the save - and is kept because that question comes back after every game update. The recipe itself
    /// now lives in <see cref="PuppetFactory"/>, which is what the session uses for real; a second copy
    /// here would be the one that quietly stops matching.
    ///
    /// Debug-only. <b>Use a throwaway save.</b>
    /// </summary>
    internal static class PuppetSpike
    {
        private static Actor? _puppet;

        /// <summary>
        /// Spawns the puppet, or despawns it if one is already out.
        /// </summary>
        internal static void Toggle()
        {
            if (_puppet != null)
            {
                PuppetFactory.Destroy(_puppet);
                _puppet = null;
                MelonLogger.Msg("[Puppet] spike body despawned.");
                return;
            }

            try
            {
                Spawn();
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Puppet] spike spawn failed: " + e);
            }
        }

        private static void Spawn()
        {
            if (!PlayerAccess.TryGetPlayer(out Actor player))
            {
                MelonLogger.Warning("[Puppet] no player yet; load a world first.");
                return;
            }

            Vector3 beside = player.transform.position + new Vector3(2f, 0f, 0f);
            if (!PuppetFactory.TryCreate("DrovaCoop.SpikePuppet", beside, out _puppet) || _puppet == null)
            {
                return;
            }

            Report(player, _puppet);
        }

        /// <summary>
        /// Reports what the clone looks like to the game, which is the actual result of the spike.
        /// </summary>
        private static void Report(Actor player, Actor puppet)
        {
            try
            {
                MelonLogger.Msg($"[Puppet] real player IsPlayer={player.IsPlayer}, puppet IsPlayer={puppet.IsPlayer}");
                MelonLogger.Msg("[Puppet] IsPlayer true on the puppet is expected: it is derived from the EntityInfo GUID, " +
                    "which a clone shares. What must stay false is CustomStats.IsPlayer, set only by SetPlayerActor.");

                // Whether it rendered is the actual exit criterion, and the log has to be able to answer
                // it: "spawned" only means the object exists. A clone that is active with enabled
                // renderers but invisible on screen is a different problem - sorting layer, camera, or
                // the chunk it sits in - than one that never activated.
                int renderers = 0;
                int enabled = 0;
                foreach (Renderer renderer in puppet.gameObject.GetComponentsInChildren<Renderer>(true))
                {
                    renderers++;
                    if (renderer.enabled) enabled++;
                }

                float distance = Vector3.Distance(player.transform.position, puppet.transform.position);
                MelonLogger.Msg($"[Puppet] active={puppet.gameObject.activeInHierarchy}, renderers={enabled}/{renderers} enabled, " +
                    $"{distance:0.0} units from the player, scene '{puppet.gameObject.scene.name}'");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[Puppet] report failed: " + e.Message);
            }
        }
    }
}
#endif
