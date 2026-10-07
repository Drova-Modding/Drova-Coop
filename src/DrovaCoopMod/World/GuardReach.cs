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
    /// Makes player-only trigger volumes notice a teammate's body. They poll only the local player, never physics.
    /// A crime volume runs on the trespasser's own machine and tells the guards they saw it. Aggro and NPC-port
    /// volumes act on host-side brains, so the host drives them for a teammate's body. Other volume kinds (teleports,
    /// scene changes, quests) are never driven, and one player holds a volume at a time since a crime module holds one crime.
    /// </summary>
    internal static class GuardReach
    {
        /// <summary>Which teammate holds a volume. Keyed by instance id because native pointers get reused.</summary>
        private static readonly Dictionary<int, Held> Holder = new();

        private static readonly Dictionary<int, VolumeKind> Kinds = new();

        private static readonly HitTriggerArgs Args = new();

        private static readonly List<int> Lapsed = new();

        /// <summary>One volume a teammate is standing in.</summary>
        private readonly struct Held
        {
            internal Held(PlayerId player, HitReceiveHandler handler, int seen)
            {
                Player = player;
                Handler = handler;
                Seen = seen;
            }

            internal PlayerId Player { get; }

            internal HitReceiveHandler Handler { get; }

            /// <summary>The frame it was last polled in, so an unloaded volume is noticed.</summary>
            internal int Seen { get; }
        }

        private static int _frame;

        /// <summary>Frames a held volume may go unpolled before its crime is ended anyway.</summary>
        private const int LapseFrames = 30;

        /// <summary>Whether the poll patch installed.</summary>
        internal static bool Hooked { get; private set; }

        /// <summary>How many times a teammate's body set a crime volume off. For <c>coop_status</c>.</summary>
        internal static int Entered { get; private set; }

        /// <summary>How many times a teammate's body left one again.</summary>
        internal static int Left { get; private set; }

        /// <summary>How many volumes a teammate's body is standing in right now.</summary>
        internal static int Holding => Holder.Count;

        /// <summary>How many held volumes went away before their crime could be ended properly.</summary>
        internal static int Abandoned { get; private set; }

        /// <summary>Applies the patches. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooked = Hooking.TryPostfix(harmony, typeof(HitReceiveHandler), nameof(HitReceiveHandler.ManagedUpdate),
                typeof(GuardReach), nameof(ManagedUpdatePostfix));

            // A player-only volume is polled only while the camera keeps it loaded.
            Hooking.TryPostfix(harmony, typeof(HitReceiveHandler), nameof(HitReceiveHandler.GetLoadPriority),
                typeof(GuardReach), nameof(LoadPriorityPostfix));

            if (!Hooked)
            {
                MelonLogger.Warning("[Guard] the trigger-volume poll could not be patched; guards will ignore teammates.");
            }
        }

        /// <summary>Ends a crime whose volume unloaded and stopped being polled. Called once a frame.</summary>
        internal static void Update()
        {
            _frame++;

            if (Holder.Count == 0) return;

            Lapsed.Clear();

            foreach (KeyValuePair<int, Held> entry in Holder)
            {
                if (_frame - entry.Value.Seen <= LapseFrames) continue;

                Lapsed.Add(entry.Key);
            }

            foreach (int id in Lapsed)
            {
                if (!Holder.TryGetValue(id, out Held held)) continue;

                Holder.Remove(id);

                if (ReleaseHeld(held.Handler, held)) continue;

                Abandoned++;
            }

            Lapsed.Clear();
        }

        /// <summary>Forgets every held volume. Called when a session ends.</summary>
        internal static void Reset()
        {
            Holder.Clear();
            Kinds.Clear();
            Lapsed.Clear();
            Entered = 0;
            Left = 0;
            Abandoned = 0;
        }

        private static void ManagedUpdatePostfix(HitReceiveHandler __instance)
        {
            if (RemotePlayers.Bodies.Count == 0) return;
            try
            {
                // Physics volumes already see remote bodies.
                if (!__instance._onlyCheckWithPlayer) return;

                if (CoopSession.State == CoopSessionState.Joined)
                {
                    if (__instance._isInCollider && Kind(__instance) == VolumeKind.Crime) Witness(__instance);
                    return;
                }

                if (CoopSession.State != CoopSessionState.Hosting || Kind(__instance) != VolumeKind.HostDriven) return;
                Drive(__instance);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Guard] driving a crime volume for a teammate failed: " + e);
            }
        }

        private static void Drive(HitReceiveHandler handler)
        {
            int id = handler.GetInstanceID();

            // The local player takes precedence. The teammate's crime is exited first, since the module holds one crime.
            if (handler._isInCollider)
            {
                if (Holder.TryGetValue(id, out Held taken))
                {
                    Holder.Remove(id);
                    ReleaseHeld(handler, taken);
                }

                return;
            }

            bool hasHeld = Holder.TryGetValue(id, out Held held);

            RemotePlayer? occupant = hasHeld ? Find(held.Player) : null;

            // The holder's body is gone, so there is nobody to run an exit for.
            if (hasHeld && occupant == null)
            {
                Holder.Remove(id);
                return;
            }

            if (occupant != null)
            {
                Continue(handler, id, occupant, held.Player);
                return;
            }

            foreach (KeyValuePair<PlayerId, RemotePlayer> body in RemotePlayers.Bodies)
            {
                if (body.Value == null || body.Value.Body == null) continue;
                if (!Overlaps(handler, body.Value.Body)) continue;

                // The volume's own filters (quest state, faction), asked about the local player since they test for the player:
                // skipping them aggroed Red Tower's attackers onto a teammate.
                if (!Drova_Modding_API.Access.PlayerAccess.TryGetPlayer(out Actor player)) return;
                Bind(Args, player);
                if (!handler.CheckFilterWithBhvrs(Args)) return;

                Bind(Args, body.Value.Body);
                // The flag is the local player's, borrowed for the call and handed back as false.
                handler._isInCollider = false;
                handler.TriggerBhvrsEnter(Args);
                handler._isInCollider = false;

                Holder[id] = new Held(body.Key, handler, _frame);
                Entered++;
                return;
            }
        }

        /// <summary>Keeps a volume ticking for the body that holds it, and exits when the body leaves.</summary>
        private static void Continue(HitReceiveHandler handler, int id, RemotePlayer occupant, PlayerId held)
        {
            Bind(Args, occupant.Body);

            bool inside = Overlaps(handler, occupant.Body);

            handler._isInCollider = true;

            if (inside)
            {
                handler.TriggerBhvrsUpdate(Args);
                Holder[id] = new Held(held, handler, _frame);
            }
            else
            {
                handler.TriggerBhvrsExit(Args, true);
                Holder.Remove(id);
                Left++;
            }

            handler._isInCollider = false;
        }

        /// <summary>Runs the authored exit for the teammate holding a volume.</summary>
        /// <returns>False when there was nothing left to run it against.</returns>
        private static bool ReleaseHeld(HitReceiveHandler handler, Held held)
        {
            RemotePlayer? occupant = Find(held.Player);
            if (handler == null || occupant == null || occupant.Body == null) return false;

            try
            {
                Bind(Args, occupant.Body);
                handler._isInCollider = true;
                handler.TriggerBhvrsExit(Args, true);
                handler._isInCollider = false;
                Left++;
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Guard] ending a teammate's crime on a volume failed: " + e);
                return false;
            }
        }

        /// <summary>The game's own overlap test, with its feet-point fallback for a disabled (downed) collider.</summary>
        internal static bool Overlaps(HitReceiveHandler handler, Actor body)
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

        private enum VolumeKind : byte { Other, Crime, HostDriven }

        /// <summary>Host: keeps a host-driven volume loaded while a teammate's body is inside its rect.</summary>
        private static void LoadPriorityPostfix(HitReceiveHandler __instance, ref Il2CppDrova.Utilities.LazyLoading.LazyPriority __result)
        {
            try
            {
                if (__result != Il2CppDrova.Utilities.LazyLoading.LazyPriority.Idle) return;
                if (CoopSession.State != CoopSessionState.Hosting || RemotePlayers.Bodies.Count == 0) return;
                if (__instance == null || !__instance._onlyCheckWithPlayer || !__instance.gameObject.activeInHierarchy) return;
                if (Kind(__instance) != VolumeKind.HostDriven) return;

                Rect rect = __instance._localRect;
                rect.center += (Vector2)__instance.transform.position;

                foreach (RemotePlayer body in RemotePlayers.Bodies.Values)
                {
                    if (body?.Body == null || !rect.Contains(body.Body.GetFeetPosition())) continue;

                    __result = Il2CppDrova.Utilities.LazyLoading.LazyPriority.Primary;
                    return;
                }
            }
            catch
            {
            }
        }

        /// <summary>Guards told on a client that they saw this player trespass. For tests.</summary>
        internal static int Witnessed { get; private set; }

        private const float WitnessRange = 600f;

        /// <summary>Whether the crime volume names this guard among those who react to it.</summary>
        private static bool Names(Il2CppDrova.Crimes.CrimeModule module, Actor guard)
        {
            string? info = guard.GetEntityInfo()?.GUID;
            if (string.IsNullOrEmpty(info)) return false;

            foreach (Il2CppSystem.Collections.Generic.List<EntityInfo>? list in new[] { module._entitiesToReact, module._dynamicEntitiesToReact })
            {
                if (list == null) continue;
                for (int index = 0; index < list.Count; index++)
                {
                    if (list[index]?.GUID == info) return true;
                }
            }

            return false;
        }

        /// <summary>Classifies a volume by all its behaviors, since enter/exit fire the whole list.</summary>
        private static VolumeKind Kind(HitReceiveHandler handler)
        {
            int id = handler.GetInstanceID();
            if (Kinds.TryGetValue(id, out VolumeKind known)) return known;

            Il2CppSystem.Collections.Generic.List<AHitReceiveBhvr> bhvrs = handler.GetHitReceiveBhvrs();
            bool crime = bhvrs != null && bhvrs.Count > 0;
            bool driven = crime;

            for (int index = 0; bhvrs != null && index < bhvrs.Count; index++)
            {
                AHitReceiveBhvr bhvr = bhvrs[index];
                if (bhvr == null) continue;

                crime &= bhvr.TryCast<HitReceiveBhvr_Crime>() != null;
                driven &= bhvr.TryCast<HitReceiveBhvr_SetAggroToActor>() != null
                    || bhvr.TryCast<HitReceiveBhvr_PortActorsToCustomPoint>() != null;
            }

            VolumeKind kind = crime ? VolumeKind.Crime : driven ? VolumeKind.HostDriven : VolumeKind.Other;
            Kinds[id] = kind;
            return kind;
        }

        /// <summary>Client: tells the nearby guard copies, which have no AI here, that they saw this player's crime.</summary>
        private static void Witness(HitReceiveHandler handler)
        {
            if (!Drova_Modding_API.Access.PlayerAccess.TryGetPlayer(out Actor player)) return;
            Vector2 at = player.GetFeetPosition();

            Il2CppSystem.Collections.Generic.List<AHitReceiveBhvr> bhvrs = handler.GetHitReceiveBhvrs();
            for (int index = 0; index < bhvrs.Count; index++)
            {
                Il2CppDrova.Crimes.CrimeModule? module = bhvrs[index]?.TryCast<HitReceiveBhvr_Crime>()?._crime;
                Il2CppDrova.Crimes.CrimeHandler? crime = module?._activeCrimeObj;
                Il2CppDrova.Crimes.ICrimeService? service = crime?.GetCrimeService();
                if (service == null) continue;

                foreach (KeyValuePair<EnemyNetId, Actor> bound in EnemyRegistry.BoundBodies)
                {
                    Actor guard = bound.Value;
                    if (guard == null || ((Vector2)guard.GetFeetPosition() - at).sqrMagnitude > WitnessRange * WitnessRange) continue;

                    string? guid = guard.GetGuidComponent()?.GuidString;
                    if (string.IsNullOrEmpty(guid)) continue;

                    // A named guard whose copy arrived after the crime began was never put on its list.
                    if (!service.IsActorGuidReactingToThis(guid))
                    {
                        if (!Names(module!, guard)) continue;
                        crime!.AddActorReactionGuid(guid);
                    }

                    // The service dedups per guid, so asking every frame is cheap.
                    service.WitnessSawCrime(new Il2CppDrova.Crimes.CrimeActor(guard).Cast<Il2CppDrova.Crimes.ICrimeActor>());
                    if (Witnessed++ == 0)
                    {
                        MelonLogger.Msg("[Guard] a guard saw this player trespass; the reaction plays here.");
                    }
                }
            }
        }
    }
}
