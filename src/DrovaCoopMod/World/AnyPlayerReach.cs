using Drova_Modding_API;
using Drova_Modding_API.Access;
using Il2CppDrova;
using Il2CppDrova.GlobalVarSystem;
using Il2CppDrova.HitSystem;
using Il2CppDrova.Routine;
using Il2CppDrova.Utilities.LazyLoading;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Answers the story's "is the player near?" with "is any player near?", since routines resolve
    /// "the player" to this machine's own. Followers follow the nearest player, waits accept any
    /// player in range, and "away" or "left" means every player.
    /// </summary>
    internal static class AnyPlayerReach
    {
        /// <summary>How many times a teammate's body answered for "the player". For <c>coop_status</c>.</summary>
        internal static int Answered { get; private set; }

        /// <summary>How many of the patches applied.</summary>
        internal static int Hooked { get; private set; }

        /// <summary>
        /// Exit writes held while a teammate is still inside, made in the leaver's name once the last one is out.
        /// </summary>
        private static readonly List<(HitReceiveBhvr_SetGVars Bhvr, HitReceiveHandler Handler, HitTriggerArgs Args)> HeldExits = new();

        /// <summary>
        /// Volumes whose entry means "the party did this", so it is made once every player is inside.
        /// </summary>
        private static readonly HashSet<string> AllPlayersEnter = new(StringComparer.Ordinal)
        {
            "BanditMine_HitReceiver_LeaveCaveRebellion",
        };

        /// <summary>
        /// The mine's walk-in volume, which picks the raid branch for everybody. Held during a teammate's capture brawl.
        /// </summary>
        private const string MineRaidVolume = "HitReceiver_OnEnter_PlayerEntered_BrutusMIne";

        private static readonly List<(HitReceiveBhvr_SetGVars Bhvr, HitReceiveHandler Handler, HitTriggerArgs Args)> HeldEnters = new();

        private static bool _releasing;

        /// <summary>Forgets the count and any held exit. Called when a session ends.</summary>
        internal static void Reset()
        {
            Answered = 0;
            HeldExits.Clear();
            HeldEnters.Clear();
        }

        /// <summary>
        /// Makes held entries and exits once the party is in or out. Call once per frame from a loaded world.
        /// </summary>
        internal static void Update()
        {
            ReleaseHeldEnters();

            for (int index = HeldExits.Count - 1; index >= 0; index--)
            {
                (HitReceiveBhvr_SetGVars bhvr, HitReceiveHandler handler, HitTriggerArgs args) = HeldExits[index];

                try
                {
                    // Came back in, or the volume unloaded: the exit is not owed.
                    if (bhvr == null || handler == null || handler._isInCollider)
                    {
                        HeldExits.RemoveAt(index);
                        continue;
                    }

                    if (IsTeammateInside(handler)) continue;

                    HeldExits.RemoveAt(index);

                    _releasing = true;
                    bhvr.OnExit(args);
                }
                catch (Exception e)
                {
                    HeldExits.RemoveAt(index);
                    MelonLogger.Error("[Reach] making a held volume exit failed: " + e);
                }
                finally
                {
                    _releasing = false;
                }
            }
        }

        /// <summary>Applies the patches. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            // DistanceCheckService.Update is inlined, so range checks are patched one level up.
            if (Hooking.TryPrefix(harmony, typeof(FollowActorPoint), "get_Position",
                typeof(AnyPlayerReach), nameof(FollowPositionPrefix))) Hooked++;

            if (Hooking.TryPostfix(harmony, typeof(WaitRangeEntityRoutineCommand), "OnExecuteCommand",
                typeof(AnyPlayerReach), nameof(WaitRangePostfix))) Hooked++;

            if (Hooking.TryPrefix(harmony, typeof(IsEntityInRangeCommand), "OnExecuteCommand",
                typeof(AnyPlayerReach), nameof(InRangePrefix))) Hooked++;

            if (Hooking.TryPrefix(harmony, typeof(GlobalObserverBhvr_PlayerAway), "ManagedUpdate",
                typeof(AnyPlayerReach), nameof(PlayerAwayPrefix))) Hooked++;

            if (Hooking.TryPrefix(harmony, typeof(HitReceiveBhvr_SetGVars), nameof(HitReceiveBhvr_SetGVars.OnExit),
                typeof(AnyPlayerReach), nameof(SetGVarsExitPrefix))) Hooked++;

            if (Hooking.TryPrefix(harmony, typeof(HitReceiveBhvr_SetGVars), nameof(HitReceiveBhvr_SetGVars.OnEnter),
                typeof(AnyPlayerReach), nameof(SetGVarsEnterPrefix))) Hooked++;

            if (Hooking.TryPrefix(harmony, typeof(HitFilter_HitOnce_Save), "UpdateState",
                typeof(AnyPlayerReach), nameof(HitOncePrefix))) Hooked++;

            // A leading NPC (Molvina to the Red Tower) runs on only while "the player" keeps up.
            if (Hooking.TryPostfix(harmony, typeof(Il2CppDrova.AI.Graphs.IsPlayerInRangeCondition), "OnCheck",
                typeof(AnyPlayerReach), nameof(PlayerInRangePostfix))) Hooked++;

            if (Hooked < 8)
            {
                MelonLogger.Warning($"[Reach] {Hooked} of 8 any-player patches applied; followers, waiting " +
                    "NPCs or \"player went away\" beats may only see this machine's own player.");
            }
        }

        /// <summary>
        /// Follows the nearest player instead of this machine's own.
        /// </summary>
        private static bool FollowPositionPrefix(FollowActorPoint __instance, ref Vector2 __result)
        {
            if (RemotePlayers.Count == 0) return true;

            try
            {
                if (!IsPlayerInfo(__instance._entity) || !PlayerAccess.TryGetPlayer(out Actor local)) return true;

                Vector2 follower = __instance._routineEntity.GetPosition();
                Actor? nearer = NearestRemoteBody(follower, (local.GetFeetPosition() - follower).sqrMagnitude);

                if (nearer != null) Answered++;

                // Always answered here, since the game's "first entity with the player's info" can be a
                // teammate's body. The game's own placement math keeps the authored offsets.
                __result = __instance.CalcPositionFromEntity(nearer ?? local);
                return false;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Lets a waiting NPC stop waiting when a teammate, rather than this machine's player, is the one
        /// in range.
        /// </summary>
        private static void WaitRangePostfix(WaitRangeEntityRoutineCommand __instance, RoutineCommandArgs args,
            ref bool __result)
        {
            if (__result || RemotePlayers.Count == 0) return;

            try
            {
                if (!TeammateSatisfies(__instance._entities, __instance._range, args)) return;

                Answered++;
                __result = true;
            }
            catch
            {
                // Fail closed: the NPC keeps waiting, as it would alone.
            }
        }

        /// <summary>
        /// Lets a leading NPC go on when a teammate, rather than this machine's player, keeps up with it.
        /// </summary>
        private static void PlayerInRangePostfix(Il2CppDrova.AI.Graphs.IsPlayerInRangeCondition __instance, ref bool __result)
        {
            if (__result || RemotePlayers.Count == 0) return;

            try
            {
                Actor? leader = __instance._actorFactory?.GetValue()?.GetActor()?.GetObject()?.TryCast<Actor>();
                if (leader == null) return;

                // The game's own tolerance: range times its hysteresis offset.
                float range = __instance._distanceToCheck.GetValue() * __instance._offset;
                if (NearestRemoteBody(leader.GetFeetPosition(), range * range) == null) return;

                Answered++;
                __instance._lastCheck = true;
                __result = true;
            }
            catch
            {
                // Fail closed: the NPC waits for this machine's player, as it would alone.
            }
        }

        /// <summary>
        /// The one-shot range check. Decided in a prefix, since its failure branch writes a verdict.
        /// </summary>
        private static bool InRangePrefix(IsEntityInRangeCommand __instance, RoutineCommandArgs args,
            ref bool __result)
        {
            if (RemotePlayers.Count == 0) return true;

            try
            {
                if (!TeammateSatisfies(__instance._entities, __instance._range, args)) return true;

                Answered++;

                IRoutineCommand success = __instance._successCommand;
                __result = success == null || success.ExecuteCommand(args);
                return false;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Whether a range check naming the player passes with a teammate standing in for them.
        /// </summary>
        private static bool TeammateSatisfies(Il2CppSystem.Collections.Generic.List<EntityInfo> entities,
            float range, RoutineCommandArgs args)
        {
            if (entities == null || args?.Actor == null) return false;

            Vector2 here = args.Actor.GetPosition();
            bool namesPlayer = false;

            for (int index = 0; index < entities.Count; index++)
            {
                EntityInfo info = entities[index];

                if (IsPlayerInfo(info))
                {
                    namesPlayer = true;
                    continue;
                }

                Entity? other = info == null ? null : info.GetActorFromInfo();
                if (other != null && (other.GetFeetPosition() - here).magnitude >= range) return false;
            }

            return namesPlayer && RemotePlayers.IsAnyWithin(here, range);
        }

        /// <summary>
        /// Holds a "player went away" observer while a teammate is still within its distance.
        /// </summary>
        private static bool PlayerAwayPrefix(GlobalObserverBhvr_PlayerAway __instance)
        {
            if (RemotePlayers.Count == 0) return true;

            try
            {
                LazyActor? watched = __instance._entityToCheckDistance?.GetLazyActorFromInfo();
                if (watched == null) return true;

                if (!RemotePlayers.IsAnyWithin(watched.GetFeetPosition(), __instance._distanceToPlayer)) return true;

                Answered++;
                return false;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Holds a player-only volume's exit write while a teammate is still inside. <see cref="Update"/> makes it later.
        /// </summary>
        private static bool SetGVarsExitPrefix(HitReceiveBhvr_SetGVars __instance, HitTriggerArgs args)
        {
            if (_releasing || RemotePlayers.Count == 0) return true;

            try
            {
                if (!AHitReceiveBhvr.HasFlag(__instance._when, AHitReceiveBhvr.When.OnExit)) return true;
                if (!RemotePlayers.IsLocalPlayer(args?.Entity)) return true;

                HitReceiveHandler handler = __instance.GetComponentInParent<HitReceiveHandler>();
                if (handler == null || !handler._onlyCheckWithPlayer || !IsTeammateInside(handler)) return true;

                foreach ((HitReceiveBhvr_SetGVars held, _, _) in HeldExits)
                {
                    if (held.Pointer == __instance.Pointer) return false;
                }

                // A copy: the handler reuses its args object for every poll.
                HitTriggerArgs kept = new();
                kept.Entity = args!.Entity;
                kept.Flags.Add(HitTriggerFlag.Entity);

                HeldExits.Add((__instance, handler, kept));
                Answered++;
                return false;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Holds a party-wide entry until every player is inside, and the mine's raid entry while a
        /// teammate's capture brawl is running.
        /// </summary>
        private static bool SetGVarsEnterPrefix(HitReceiveBhvr_SetGVars __instance, HitTriggerArgs args)
        {
            if (_releasing || RemotePlayers.Count == 0) return true;

            try
            {
                if (!RemotePlayers.IsLocalPlayer(args?.Entity)) return true;

                HitReceiveHandler handler = __instance.GetComponentInParent<HitReceiveHandler>();
                if (handler == null) return true;

                string name = handler.gameObject.name;

                if (name == MineRaidVolume)
                {
                    // Every player's own brawl flag too, since a client knows only the brawls the host announces.
                    if (!TeamBrawls.AnyHostBrawl && !EnemyEngagements.AnyLeasedBrawl
                        && !RemotePlayers.Bodies.Values.Any(other => other != null && other.IsInBrawl)) return true;

                    Answered++;
                    MelonLogger.Msg("[Reach] walked into the mine while a teammate's capture brawl runs; " +
                        "the raid branch was not taken. The capture decides.");
                    return false;
                }

                if (!AllPlayersEnter.Contains(name) || AreAllTeammatesInside(handler)) return true;

                foreach ((HitReceiveBhvr_SetGVars held, _, _) in HeldEnters)
                {
                    if (held.Pointer == __instance.Pointer) return false;
                }

                HitTriggerArgs kept = new();
                kept.Entity = args!.Entity;
                kept.Flags.Add(HitTriggerFlag.Entity);

                HeldEnters.Add((__instance, handler, kept));
                Answered++;
                return false;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Keeps a hit-once volume unspent while its entry is held, or the first player in switches it off for good.
        /// </summary>
        private static bool HitOncePrefix(HitFilter_HitOnce_Save __instance)
        {
            if (_releasing) return true;

            try
            {
                HitReceiveHandler handler = __instance._handler;
                if (handler == null) return true;

                foreach ((_, HitReceiveHandler held, _) in HeldEnters)
                {
                    if (held != null && held.Pointer == handler.Pointer) return false;
                }

                return true;
            }
            catch
            {
                return true;
            }
        }

        private static void ReleaseHeldEnters()
        {
            for (int index = HeldEnters.Count - 1; index >= 0; index--)
            {
                (HitReceiveBhvr_SetGVars bhvr, HitReceiveHandler handler, HitTriggerArgs args) = HeldEnters[index];

                try
                {
                    if (bhvr == null || handler == null || !handler._isInCollider)
                    {
                        HeldEnters.RemoveAt(index);
                        continue;
                    }

                    if (!AreAllTeammatesInside(handler)) continue;

                    HeldEnters.RemoveAt(index);

                    _releasing = true;
                    bhvr.OnEnter(args);

                    // Spend the volume, as the vanilla entry would have.
                    foreach (HitFilter_HitOnce_Save once in handler.GetComponentsInChildren<HitFilter_HitOnce_Save>(true))
                    {
                        once.UpdateState(args);
                    }
                }
                catch (Exception e)
                {
                    HeldEnters.RemoveAt(index);
                    MelonLogger.Error("[Reach] making a held volume entry failed: " + e);
                }
                finally
                {
                    _releasing = false;
                }
            }
        }

        private static bool AreAllTeammatesInside(HitReceiveHandler handler)
        {
            foreach (RemotePlayer player in RemotePlayers.Bodies.Values)
            {
                if (player?.Body != null && !GuardReach.Overlaps(handler, player.Body)) return false;
            }

            return true;
        }

        private static bool IsTeammateInside(HitReceiveHandler handler)
        {
            foreach (RemotePlayer player in RemotePlayers.Bodies.Values)
            {
                if (player?.Body != null && GuardReach.Overlaps(handler, player.Body)) return true;
            }

            return false;
        }

        private static bool IsPlayerInfo(EntityInfo? info)
        {
            if (info == null || !PlayerAccess.TryGetPlayer(out Actor local)) return false;

            EntityInfo? mine = local.GetEntityInfo();
            return mine != null && mine.Pointer == info.Pointer;
        }

        private static Actor? NearestRemoteBody(Vector2 point, float beatSquared)
        {
            Actor? best = null;

            foreach (RemotePlayer player in RemotePlayers.Bodies.Values)
            {
                Actor body = player.Body;
                if (body == null) continue;

                float squared = (body.GetFeetPosition() - point).sqrMagnitude;
                if (squared >= beatSquared) continue;

                beatSquared = squared;
                best = body;
            }

            return best;
        }
    }
}
