using Drova_Modding_API;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.HitSystem;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Lets a guard notice the other player walking where they should not.
    ///
    /// A forbidden area is a trigger volume with a crime behavior on it, and the game runs the common
    /// ones without physics at all: when the volume is marked as only ever mattering to the player, the
    /// handler switches its own rigidbody off and instead polls one cached actor every frame. That actor
    /// is whoever the entity handler registered as the player, which on any machine is that machine's own
    /// player and nobody else. A remote body can stand in the middle of the volume for as long as it
    /// likes and the volume never fires, so no crime is ever created and the guard has nothing to react
    /// to - his brain is working perfectly on a world where the trespass did not happen.
    ///
    /// So the same overlap test is run again here for the other players' bodies, on the host, which is
    /// where the guards think. The game's own enter, update and exit calls do the rest, which means the
    /// crime, the warning dialogue and the shove are the authored ones rather than an imitation.
    ///
    /// Two deliberate narrowings. Only volumes carrying a crime behavior are driven: the same handler
    /// shape also backs teleports, scene changes, quest completions and weather, and firing those for a
    /// body that is not the local player would move the wrong world. And only one player holds a volume
    /// at a time, the local player first, because a crime module holds exactly one crime and a second
    /// entrant would either be dropped or would cancel the first one's on the way out.
    /// </summary>
    internal static class GuardReach
    {
        /// <summary>Which remote player currently holds a volume, by handler.</summary>
        private static readonly Dictionary<IntPtr, PlayerId> _holder = new();

        /// <summary>Whether a handler is one of the crime volumes, decided once per handler.</summary>
        private static readonly Dictionary<IntPtr, bool> _isCrimeVolume = new();

        /// <summary>Reused rather than allocated per volume per frame; the game pools its own for the same reason.</summary>
        private static readonly HitTriggerArgs _args = new();

        private static bool _hooked;

        private static int _entered;

        private static int _left;

        /// <summary>Whether the poll patch installed. Without it nothing here runs.</summary>
        internal static bool Hooked => _hooked;

        /// <summary>How many times a teammate's body set a crime volume off. For <c>coop_status</c>.</summary>
        internal static int Entered => _entered;

        /// <summary>How many times a teammate's body left one again.</summary>
        internal static int Left => _left;

        /// <summary>How many volumes a teammate's body is standing in right now.</summary>
        internal static int Holding => _holder.Count;

        /// <summary>Applies the patch. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            // The handler's update, which the update manager reaches through IUpdatable, so it is an
            // interface implementation and cannot have been inlined away.
            _hooked = Hooking.TryPostfix(harmony, typeof(HitReceiveHandler), nameof(HitReceiveHandler.ManagedUpdate),
                typeof(GuardReach), nameof(ManagedUpdatePostfix));

            if (!_hooked)
            {
                MelonLogger.Warning("[Guard] the trigger-volume poll could not be patched; guards will ignore teammates.");
            }
        }

        /// <summary>Forgets every held volume. Called when a session ends.</summary>
        internal static void Reset()
        {
            _holder.Clear();
            _isCrimeVolume.Clear();
            _entered = 0;
            _left = 0;
        }

        private static void ManagedUpdatePostfix(HitReceiveHandler __instance)
        {
            // Host only. The crime has to exist on the machine running the guard's brain, and on a client
            // every authored NPC is a driven copy with nothing behind its eyes.
            if (CoopSession.State != CoopSessionState.Hosting) return;
            if (RemotePlayers.Bodies.Count == 0) return;

            try
            {
                // The physics path is per entity already and picks a remote body up on its own; only the
                // volumes that opted out of physics need this.
                if (!__instance._onlyCheckWithPlayer) return;
                if (!IsCrimeVolume(__instance)) return;

                Drive(__instance);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Guard] driving a crime volume for a teammate failed: " + e);
            }
        }

        private static void Drive(HitReceiveHandler handler)
        {
            IntPtr id = handler.Pointer;

            // The local player is standing in it, so the crime is already theirs. Anything done here
            // would either be refused by the crime module or would end the local player's crime.
            if (handler._isInCollider)
            {
                _holder.Remove(id);
                return;
            }

            _holder.TryGetValue(id, out PlayerId held);
            bool hasHeld = _holder.ContainsKey(id);

            RemotePlayer? occupant = hasHeld
                ? Find(held)
                : null;

            // The body that held it has gone, so the volume is free again without an exit being owed to
            // anybody; there is no body left to name in one.
            if (hasHeld && occupant == null)
            {
                _holder.Remove(id);
                return;
            }

            if (occupant != null)
            {
                Continue(handler, id, occupant, held);
                return;
            }

            foreach (KeyValuePair<PlayerId, RemotePlayer> body in RemotePlayers.Bodies)
            {
                if (body.Value == null || body.Value.Body == null) continue;
                if (!Overlaps(handler, body.Value.Body)) continue;

                Bind(_args, body.Value.Body);

                if (!handler.CheckFilterWithBhvrs(_args)) continue;

                // The flag belongs to the local player's own state machine, so it is borrowed for the
                // call and handed back as false: the early return above already established that the
                // local player is not inside this volume.
                handler._isInCollider = false;
                handler.TriggerBhvrsEnter(_args);
                handler._isInCollider = false;

                _holder[id] = body.Key;
                _entered++;
                return;
            }
        }

        /// <summary>
        /// Keeps a volume ticking for the body that holds it, and hands it back when the body leaves or
        /// stops qualifying.
        /// </summary>
        private static void Continue(HitReceiveHandler handler, IntPtr id, RemotePlayer occupant, PlayerId held)
        {
            Bind(_args, occupant.Body);

            bool inside = Overlaps(handler, occupant.Body) && handler.CheckFilterWithBhvrs(_args);

            handler._isInCollider = true;

            if (inside)
            {
                handler.TriggerBhvrsUpdate(_args);
            }
            else
            {
                handler.TriggerBhvrsExit(_args, true);
                _holder.Remove(id);
                _left++;
            }

            handler._isInCollider = false;
        }

        /// <summary>
        /// The game's own overlap test, repeated for a body it does not consider. The cheap point test
        /// is the fallback the game uses itself when a body's collider is switched off, which is what a
        /// downed or knocked-out body looks like.
        /// </summary>
        private static bool Overlaps(HitReceiveHandler handler, Actor body)
        {
            Collider2D receive = handler.GetReceiveCollider();
            if (receive == null) return false;

            Collider2D collider = body.GetColliderModule()?.GetCollider();
            if (collider == null) return receive.OverlapPoint(body.GetFeetPosition());

            return collider.enabled
                ? receive.Distance(collider).isOverlapped
                : receive.OverlapPoint(body.GetFeetPosition());
        }

        private static void Bind(HitTriggerArgs args, Actor body)
        {
            args.Entity = body;
            args.Flags.Clear();
            args.Flags.Add(HitTriggerFlag.Entity);
        }

        private static RemotePlayer? Find(PlayerId id)
        {
            if (!RemotePlayers.Bodies.TryGetValue(id, out RemotePlayer? body)) return null;

            return body != null && body.Body != null ? body : null;
        }

        /// <summary>
        /// Whether a volume carries a crime behavior. Decided once per handler and remembered, because
        /// the answer is a property of authored content and this question is asked every frame.
        /// </summary>
        private static bool IsCrimeVolume(HitReceiveHandler handler)
        {
            IntPtr id = handler.Pointer;
            if (_isCrimeVolume.TryGetValue(id, out bool known)) return known;

            bool found = false;
            Il2CppSystem.Collections.Generic.List<AHitReceiveBhvr> bhvrs = handler.GetHitReceiveBhvrs();

            if (bhvrs != null)
            {
                for (int index = 0; index < bhvrs.Count; index++)
                {
                    AHitReceiveBhvr bhvr = bhvrs[index];
                    if (bhvr == null) continue;

                    if (bhvr.TryCast<HitReceiveBhvr_Crime>() == null) continue;

                    found = true;
                    break;
                }
            }

            _isCrimeVolume[id] = found;
            return found;
        }
    }
}
