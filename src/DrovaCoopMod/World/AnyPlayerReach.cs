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
    /// Answers the story's "is the player near?" with "is any player near?".
    ///
    /// The routine and observer code resolves "the player" through
    /// <c>EntityGameHandler.GetFirstEntityByInfo(EntityInfo_Player)</c> or <c>PlayerActor</c>, which on
    /// every machine is that machine's own player. On the machine running an NPC's brain - the host, for
    /// every authored NPC near it - a teammate therefore does not exist to the story:
    ///
    /// - Liesl, Reija, the rebels and the other followers follow the host however far away it is,
    ///   while the player who fed or freed them stands beside them.
    /// - An NPC waiting for "the player" to come close (Lanzo, Eugen, Jero, Faol and about twenty more
    ///   routine points) waits for the host, and the carpenter's test fails a client who is waiting.
    /// - A "player went away" observer skips a beat or ports its NPC while a teammate stands there.
    ///
    /// - An escort's "you left" volume fails the quest for both the moment either player steps out,
    ///   while the other is still walking beside Teigen, Liesl or the expedition.
    ///
    /// One rule: a follower follows the nearest player, a wait is satisfied by any player in range, and
    /// "away" or "left" means every player is away or has left. Nothing changes while nobody else is here.
    /// </summary>
    internal static class AnyPlayerReach
    {
        /// <summary>How many times a teammate's body answered for "the player". For <c>coop_status</c>.</summary>
        internal static int Answered { get; private set; }

        /// <summary>How many of the six patches applied.</summary>
        internal static int Hooked { get; private set; }

        /// <summary>
        /// Exit writes held back because a teammate was still inside the volume, with the player who left
        /// so the write can be made in their name once the last one is out.
        /// </summary>
        private static readonly List<(HitReceiveBhvr_SetGVars Bhvr, HitReceiveHandler Handler, HitTriggerArgs Args)> _heldExits = new();

        /// <summary>
        /// Volumes whose entry means "the party did this", so it is made once every player is inside.
        /// Leaving the cave at the end of the rebellion finishes the whole mine arc; made by whoever
        /// stepped out first, it finished it for a teammate still inside with the rebels.
        /// </summary>
        private static readonly HashSet<string> AllPlayersEnter = new(StringComparer.Ordinal)
        {
            "BanditMine_HitReceiver_LeaveCaveRebellion",
        };

        /// <summary>
        /// The mine's walk-in-from-outside volume. It picks the raid branch for everybody, so it must not
        /// fire in the seconds between a teammate's capture brawl and the capture itself landing -
        /// walking in then flipped a capture already under way into a rescue.
        /// </summary>
        private const string MineRaidVolume = "HitReceiver_OnEnter_PlayerEntered_BrutusMIne";

        private static readonly List<(HitReceiveBhvr_SetGVars Bhvr, HitReceiveHandler Handler, HitTriggerArgs Args)> _heldEnters = new();

        private static bool _releasing;

        /// <summary>Forgets the count and any held exit. Called when a session ends.</summary>
        internal static void Reset()
        {
            Answered = 0;
            _heldExits.Clear();
            _heldEnters.Clear();
        }

        /// <summary>
        /// Makes each held exit once nobody is left inside, or forgets it if the player came back. Call
        /// once per frame from a loaded world.
        /// </summary>
        internal static void Update()
        {
            ReleaseHeldEnters();

            for (int index = _heldExits.Count - 1; index >= 0; index--)
            {
                (HitReceiveBhvr_SetGVars bhvr, HitReceiveHandler handler, HitTriggerArgs args) = _heldExits[index];

                try
                {
                    // Came back in, or the volume went away with its scene: the exit is simply not owed.
                    if (bhvr == null || handler == null || handler._isInCollider)
                    {
                        _heldExits.RemoveAt(index);
                        continue;
                    }

                    if (IsTeammateInside(handler)) continue;

                    _heldExits.RemoveAt(index);

                    _releasing = true;
                    bhvr.OnExit(args);
                }
                catch (Exception e)
                {
                    _heldExits.RemoveAt(index);
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
            // All six are dispatched through an interface or a vtable, so IL2CPP cannot have inlined
            // them into their callers. DistanceCheckService.Update, the obvious target for the third,
            // is a small non-virtual method and is patched one level up for exactly that reason.
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

            // A private event listener, reached through a delegate, so never inlined.
            if (Hooking.TryPrefix(harmony, typeof(HitFilter_HitOnce_Save), "UpdateState",
                typeof(AnyPlayerReach), nameof(HitOncePrefix))) Hooked++;

            // An AI-graph condition, dispatched through the task's vtable. A leader leading the way (Molvina to the
            // Red Tower's barriers) runs on only while "the player" keeps up - and that is this machine's player.
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

                // Always answered here, never left to the game. Its "first entity with the player's info"
                // is not this machine's player once a teammate's body shares that info - measured on a
                // client, whose follower stood still while its own player walked away. The game's own
                // placement math either way, so the group offset and look direction stay authored.
                __result = __instance.CalcPositionFromEntity(nearer ?? local);
                return false;
            }
            catch
            {
                // Fail open: the vanilla answer is the single-player behaviour, never a broken routine.
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
                // Fail closed to the vanilla answer: the NPC keeps waiting, as it would alone.
            }
        }

        /// <summary>
        /// Lets a leading NPC go on when a teammate, rather than this machine's player, keeps up with it. Measured
        /// on the host: Molvina stood halfway to the barrier with the client beside her, and ran on the moment the
        /// host's own player came near.
        /// </summary>
        private static void PlayerInRangePostfix(Il2CppDrova.AI.Graphs.IsPlayerInRangeCondition __instance, ref bool __result)
        {
            if (__result || RemotePlayers.Count == 0) return;

            try
            {
                Actor? leader = __instance._actorFactory?.GetValue()?.GetActor()?.GetObject()?.TryCast<Actor>();
                if (leader == null) return;

                // The game's own tolerance: the range times the hysteresis it grants once the player was in range.
                float range = __instance._distanceToCheck.GetValue() * __instance._offset;
                if (NearestRemoteBody(leader.GetFeetPosition(), range * range) == null) return;

                Answered++;
                __instance._lastCheck = true;
                __result = true;
            }
            catch
            {
                // Fail closed to the vanilla answer: the NPC waits for this machine's player, as it would alone.
            }
        }

        /// <summary>
        /// The same question asked once instead of waited on. Its failure branch writes a verdict - the
        /// carpenter's test records that the player did not wait - so it is decided before it runs.
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
        /// Whether a range check that names the player would pass with a teammate standing in for
        /// them: the player is among the entities, a teammate is in range, and every other entity the
        /// check names is in range too.
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
        /// Holds a volume's exit write while a teammate is still inside it.
        ///
        /// Only the volumes that poll the local player alone, and only when it is the local player
        /// leaving - the ones whose exit means "you left the escort", "Liesl lost you", "you left the
        /// mine". The write is not dropped: <see cref="Update"/> makes it, in the leaver's name, once
        /// the last player is out.
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

                foreach ((HitReceiveBhvr_SetGVars held, _, _) in _heldExits)
                {
                    if (held.Pointer == __instance.Pointer) return false;
                }

                // A copy: the handler reuses its args object for every poll.
                HitTriggerArgs kept = new();
                kept.Entity = args!.Entity;
                kept.Flags.Add(HitTriggerFlag.Entity);

                _heldExits.Add((__instance, handler, kept));
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
                    // Every player's own word on it: a client knew only the brawls the host announces, so with three
                    // players C walked into the raid while B's capture brawl ran, and turned B's capture into a rescue.
                    if (!TeamBrawls.AnyHostBrawl && !EnemyEngagements.AnyLeasedBrawl
                        && !RemotePlayers.Bodies.Values.Any(other => other != null && other.IsInBrawl)) return true;

                    Answered++;
                    MelonLogger.Msg("[Reach] walked into the mine while a teammate's capture brawl runs; " +
                        "the raid branch was not taken. The capture decides.");
                    return false;
                }

                if (!AllPlayersEnter.Contains(name) || AreAllTeammatesInside(handler)) return true;

                foreach ((HitReceiveBhvr_SetGVars held, _, _) in _heldEnters)
                {
                    if (held.Pointer == __instance.Pointer) return false;
                }

                HitTriggerArgs kept = new();
                kept.Entity = args!.Entity;
                kept.Flags.Add(HitTriggerFlag.Entity);

                _heldEnters.Add((__instance, handler, kept));
                Answered++;
                return false;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Keeps a hit-once volume unused while its entry is held. The filter spends itself on the
        /// handler's own update event, which our hold on the behaviour never sees: without this, the first
        /// player in used the volume up and switched it off, the held entry was dropped with it, and once
        /// every player had walked in this way the party-wide write could never happen at all - the
        /// mine's way out stayed shut for good. The release spends it instead.
        /// </summary>
        private static bool HitOncePrefix(HitFilter_HitOnce_Save __instance)
        {
            if (_releasing) return true;

            try
            {
                HitReceiveHandler handler = __instance._handler;
                if (handler == null) return true;

                foreach ((_, HitReceiveHandler held, _) in _heldEnters)
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
            for (int index = _heldEnters.Count - 1; index >= 0; index--)
            {
                (HitReceiveBhvr_SetGVars bhvr, HitReceiveHandler handler, HitTriggerArgs args) = _heldEnters[index];

                try
                {
                    // Walked back out, or the volume unloaded: the entry is not owed.
                    if (bhvr == null || handler == null || !handler._isInCollider)
                    {
                        _heldEnters.RemoveAt(index);
                        continue;
                    }

                    if (!AreAllTeammatesInside(handler)) continue;

                    _heldEnters.RemoveAt(index);

                    _releasing = true;
                    bhvr.OnEnter(args);

                    // What the vanilla entry would have done next: spend the volume.
                    foreach (HitFilter_HitOnce_Save once in handler.GetComponentsInChildren<HitFilter_HitOnce_Save>(true))
                    {
                        once.UpdateState(args);
                    }
                }
                catch (Exception e)
                {
                    _heldEnters.RemoveAt(index);
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
