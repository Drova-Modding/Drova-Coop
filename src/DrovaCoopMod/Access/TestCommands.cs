#if DEBUG
using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using Drova_Modding_API.Systems.Testing;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Enemies;
using DrovaCoopMod.Systems.GameArena;
using DrovaCoopMod.Systems.Loot;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.Systems.World;
using Il2CppDrova;
using UnityEngine;

namespace DrovaCoopMod.Access
{
    /// <summary>The test agent's commands for this mod. Debug builds only, inert unless the game runs with --agent-port.</summary>
    internal static class TestCommands
    {
        private static int _experienceMark;

        private static List<string> SpilledNearby() => PlayerAccess.TryGetPlayer(out Actor player)
            ? CacheShare.Lying(player.GetFeetPosition(), 300f)
            : throw new InvalidOperationException("no player yet");

        /// <summary>Where the save component holding this depot sits, and the key a teammate would know it by.</summary>
        private static string DepotPath(Il2CppDrova.Data.Depot_Wrapper? depot)
        {
            foreach (Il2CppDrova.Saveables.Saveable_Depot saveable in UnityEngine.Object.FindObjectsOfType<Il2CppDrova.Saveables.Saveable_Depot>())
            {
                if (saveable._depotWrapper == null || depot == null || saveable._depotWrapper.Pointer != depot.Pointer) continue;

                string path = saveable.name;
                for (Transform? node = saveable.transform.parent; node != null; node = node.parent) path = node.name + "/" + path;
                return path + " dkey=" + (WorldObjects.KeyOf(saveable) ?? "none");
            }

            return "unsaved";
        }

        private static string PathOf(Transform transform)
            => transform.parent == null ? transform.name : PathOf(transform.parent) + "/" + transform.name;

        /// <summary>A name picks the nearest living creature called like that.</summary>
        private static Actor? PoiseTarget(string target)
        {
            if (!PlayerAccess.TryGetPlayer(out Actor self)) throw new InvalidOperationException("no player yet");
            if (target == "me") return self;

            Actor[] actors = UnityEngine.Object.FindObjectsOfType<Actor>();
            if (target == "mate") return actors.FirstOrDefault(actor => RemotePlayers.IsPuppet(actor));
            if (EntityGameHandler.TryGet(out EntityGameHandler handler) && handler.GetActorFromGuid(target) is { } byGuid) return byGuid;

            Vector2 here = self.GetFeetPosition();
            return actors.Where(actor => actor != null && actor.name.Contains(target, StringComparison.OrdinalIgnoreCase)
                    && !RemotePlayers.IsPlayersBody(actor) && actor.GetHealth() is { IsDead: false })
                .OrderBy(actor => (actor.GetFeetPosition() - here).sqrMagnitude).FirstOrDefault();
        }

        internal static void Register()
        {
            // The host's loot rule, set before hosting.
            TestAgent.RegisterCommand("lootmode", mode =>
            {
                CoopPreferences.SharedLoot = mode.Trim().Equals("shared", StringComparison.OrdinalIgnoreCase);
                return CoopPreferences.SharedLoot ? "shared" : "instanced";
            });
            // The host's arena mode, set before hosting.
            TestAgent.RegisterCommand("arenamode", mode =>
            {
                CoopPreferences.ArenaMode = mode.Trim().Equals("on", StringComparison.OrdinalIgnoreCase);
                return CoopPreferences.ArenaMode ? "on" : "off";
            });
            // The host's friendly-fire rule. May change mid-session.
            TestAgent.RegisterCommand("ff", mode =>
            {
                CoopPreferences.FriendlyFire = mode.Trim().Equals("on", StringComparison.OrdinalIgnoreCase);
                return CoopPreferences.FriendlyFire ? "on" : "off";
            });
            // The help-up prompt is a held key, which the agent cannot hold. Completes the hold on every downed teammate.
            TestAgent.RegisterCommand("helpup", _ => "helped " + RemotePlayers.Bodies.Values.Count(body => body.FinishHelpUpHold()));
            // Where live objects named like the fragment stand, e.g. a creature's ability effects.
            TestAgent.RegisterCommand("where", fragment =>
            {
                string named = fragment.Trim();
                IEnumerable<string> found = UnityEngine.Object.FindObjectsOfType<Transform>()
                    .Where(transform => transform != null && transform.name.Contains(named, StringComparison.OrdinalIgnoreCase))
                    .Take(20)
                    .Select(transform => $"{transform.name}@{transform.position.x:0},{transform.position.y:0}");
                return string.Join(";", found) is { Length: > 0 } text ? text : "none";
            });
            // Zones a creature only attacks inside, like the WaterDevil's. Who stands in each, and its creatures' guids and targets.
            TestAgent.RegisterCommand("zones", _ =>
            {
                PlayerAccess.TryGetPlayer(out Actor player);
                IEnumerable<string> found = UnityEngine.Object.FindObjectsOfType<Il2CppDrova.HitSystem.HitReceiveBhvr_AddOnlyInCollider>()
                    .Where(zone => zone._handler != null && zone._handler.GetReceiveCollider() != null)
                    .Select(zone =>
                    {
                        Bounds bounds = zone._handler.GetReceiveCollider().bounds;
                        string inside = (player != null && GuardReach.Overlaps(zone._handler, player) ? "me " : "")
                            + string.Join(" ", RemotePlayers.Bodies.Where(body => body.Value.Body != null && GuardReach.Overlaps(zone._handler, body.Value.Body)).Select(body => "p" + body.Key.Value));
                        IEnumerable<string> creatures = zone._actors.ToArray().Select(lazy => lazy?.Actor == null ? "unloaded"
                            : EnemyRegistry.GuidOf(lazy.Actor) + ">" + (lazy.Actor.AIModule?.TargetService?.GetCurrentTargetActor()?.GetObject()?.TryCast<Component>()?.name ?? "-"));
                        return $"{zone._handler.name}@{bounds.center.x:0},{bounds.center.y:0} size={bounds.size.x:0}x{bounds.size.y:0} in=[{inside.Trim()}] {string.Join(",", creatures)}";
                    });
                return string.Join(";", found) is { Length: > 0 } text ? text : "none";
            });
            // A host-driven creature's blow on every teammate, sent the way DamageWatch relays one. Receivers only take it
            // from a creature they know standing near them, so the nearest bound one is named.
            TestAgent.RegisterCommand("creatureblow", amount =>
            {
                int sent = 0;
                foreach ((PlayerId teammate, RemotePlayer other) in RemotePlayers.Bodies)
                {
                    if (other.Body == null || !EnemyRegistry.TryFindNearest(other.Body.transform.position, out EnemyNetId creature)) continue;

                    RemoteDamage.ReportDamage(teammate, int.Parse(amount.Trim()), 0f, false, DamageReportMessage.TypePure, DamageReportMessage.OriginCreature, creature);
                    sent++;
                }

                return sent == 0 ? "no known creature near any teammate" : "sent to " + sent;
            });
            // Experience since "xpgain mark", as a command and a state key, so a scenario can hold one machine's gain against another's.
            TestAgent.RegisterCommand("xpgain", verb =>
            {
                PlayerAttributeStats stats = PlayerAccess.GetPlayerAttributeStats() ?? throw new InvalidOperationException("no player stats");
                if (verb.Trim() == "mark") _experienceMark = stats.ExperiencePoints;
                return (stats.ExperiencePoints - _experienceMark).ToString();
            });
            TestAgent.RegisterState(() => PlayerAccess.GetPlayerAttributeStats() is { } stats ? $"xpgain={stats.ExperiencePoints - _experienceMark}" : "xpgain=-");
            // What GateFollow reports when a story cutscene carried this player far, for a cutscene the teammate may not have loaded.
            TestAgent.RegisterCommand("storyport", name =>
            {
                RemoteCutscene.ReportCutscene(new CutsceneMessage { Name = name.Trim(), ActionId = 0 });
                return "reported " + name.Trim();
            });
            // The host's revive rule. A saved preference, so a scenario that turns it on turns it off again.
            TestAgent.RegisterCommand("standnear", mode =>
            {
                CoopPreferences.StandNearRevive = mode.Trim().Equals("on", StringComparison.OrdinalIgnoreCase);
                return CoopPreferences.StandNearRevive ? "on" : "off";
            });
            TestAgent.RegisterCommand("hostleads", mode =>
            {
                CoopPreferences.HostLeadsStory = mode.Trim().Equals("on", StringComparison.OrdinalIgnoreCase);
                return CoopPreferences.HostLeadsStory ? "on" : "off";
            });
            // A saved preference, so a scenario that raises it sets it back.
            TestAgent.RegisterCommand("enemyhealth", percent =>
            {
                CoopPreferences.EnemyHealthPercent = ushort.Parse(percent.Trim());
                return CoopPreferences.EnemyHealthPercent.ToString();
            });
            TestAgent.RegisterCommand("enemydamage", percent =>
            {
                CoopPreferences.EnemyDamagePercent = ushort.Parse(percent.Trim());
                return CoopPreferences.EnemyDamagePercent.ToString();
            });
            // So a Hardcore run can be tested on a throwaway hero.
            TestAgent.RegisterCommand("gamemode", name =>
            {
                if (string.IsNullOrWhiteSpace(name)) return Checkpoint.Mode.ToString();
                GameMode mode = Enum.Parse<GameMode>(name.Trim(), ignoreCase: true);
                if (!SavegameGameHandler.TryGet(out SavegameGameHandler? saves) || saves == null || saves.CurrentSavegame?.Data == null) throw new InvalidOperationException("no savegame loaded");
                Il2Cpp.SavegameDataExtensions.SetGameMode(saves.CurrentSavegame.Data, mode);
                // The death screen reads Savegame.Current, which need not be the handler's object.
                if (Il2CppDrova.Saveables.Savegame.Current?.Data is { } current) Il2Cpp.SavegameDataExtensions.SetGameMode(current, mode);
                return Checkpoint.Mode.ToString();
            });
            // The game's own two answers, so a GetGameMode override that its callers inlined away shows.
            TestAgent.RegisterCommand("insane", _ =>
                $"handler={Drova_Modding_API.Access.DifficultyAccess.IsInsaneMode()} access={GameModeAccess.IsInsaneMode()}");
            // creatures <name fragment> <range>. Living creatures by that name near this player, so two machines' counts compare.
            TestAgent.RegisterCommand("creatures", args =>
            {
                string[] parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !PlayerAccess.TryGetPlayer(out Actor self)) throw new InvalidOperationException("usage: creatures <name> <range>");
                float range = float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                Vector2 here = self.GetFeetPosition();
                return UnityEngine.Object.FindObjectsOfType<Actor>().Count(actor => actor != null && actor.name.Contains(parts[0], StringComparison.OrdinalIgnoreCase)
                    && !RemotePlayers.IsPlayersBody(actor) && actor.GetHealth() is { IsDead: false }
                    && (actor.GetFeetPosition() - here).sqrMagnitude <= range * range).ToString();
            });
            // Reads a shared host setting. With a value, enforces it here, which on the host sets the session's value.
            TestAgent.RegisterCommand("hostsetting", args =>
            {
                string[] parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                Drova_Modding_API.Systems.Coop.HostSetting setting = CoopAccess.HostSettings.FirstOrDefault(s => s.Key == parts[0])
                    ?? throw new InvalidOperationException("no shared setting " + parts[0]);
                if (parts.Length > 1) setting.Enforce(parts[1]);
                return setting.Read();
            });
            TestAgent.RegisterCommand("hostlan", port => CoopActions.HostDirect(int.TryParse(port, out int parsed) ? parsed : null));
            TestAgent.RegisterCommand("joindirect", CoopActions.JoinDirect);
            TestAgent.RegisterCommand("lan", _ => CoopCommands.LanList());
            // The way a click in the window does.
            TestAgent.RegisterCommand("joinlan", name =>
            {
                foreach ((Drova_Modding_API.Systems.Networking.DiscoveredHost found, Messages.LanGameInfo info) in CoopActions.FoundGames())
                {
                    if (info.HostName.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase)) return CoopActions.JoinFound(found, info);
                }

                throw new InvalidOperationException("no such game found: " + CoopCommands.LanList());
            });
            TestAgent.RegisterCommand("leave", _ => CoopActions.Leave());
            // The teammate-talk conditions one by one (Interact_Condition_IsInCombat), so a closed menu names its cause.
            TestAgent.RegisterCommand("matetalk", _ =>
            {
                if (!PlayerAccess.TryGetPlayer(out Actor self)) throw new InvalidOperationException("no player yet");
                RemotePlayer mate = RemotePlayers.Bodies.Values.FirstOrDefault(body => body?.Body != null) ?? throw new InvalidOperationException("no teammate body");
                Actor body = mate.Body!;
                return $"mateCombat={(body.AIModule.IsInCombat() ? 1 : 0)} mateBrawl={(body.CustomStats.IsInBrawl ? 1 : 0)} mateKo={(body.CustomStats.IsKnockout ? 1 : 0)} " +
                    $"selfCombat={(self.AIModule.IsInCombat() ? 1 : 0)} selfBrawl={(self.CustomStats.IsInBrawl ? 1 : 0)} " +
                    $"enemy={(body.Alignment.CheckAlignment(new Il2CppDrova.Alignment.AlignmentArgs(self).Cast<Il2CppDrova.Alignment.IAlignmentArgs>(), Il2CppDrova.Alignment.AlignmentState.Enemy) ? 1 : 0)} " +
                    $"possible={(body.CustomStats.InteractionPossible ? 1 : 0)} interactable={(body.CustomStats.IsInteractable ? 1 : 0)} " +
                    $"inDialogue={(body.DialogueModule.IsInDialogue() ? 1 : 0)} helper={(body.GetInteractionRefHelper() != null ? 1 : 0)} alive={(body.GetHealth().IsAlive ? 1 : 0)} hp={body.GetHealth().CurHealth}";
            });
            TestAgent.RegisterCommand("sfxfollow", rest => string.Join("; ", UnityEngine.Object.FindObjectsOfType<Il2CppCustomFramework.Audio.ManagedAudio>()
                .Where(audio => audio.IsPlaying && audio.GetAudioSource()?.clip != null && audio.GetAudioSource().clip.name.Contains(rest.Trim(), StringComparison.Ordinal))
                .Select(audio => audio.GetAudioSource().clip.name + " parent=" + (audio._hasParent ? 1 : 0) + " follow="
                    + (audio._followTransform == null ? "-" : PathOf(audio._followTransform) + " active=" + (audio._followTransform.gameObject.activeInHierarchy ? 1 : 0))
                    + " loop=" + (audio.GetAudioSource().loop ? 1 : 0))) is { Length: > 0 } text ? text : "none");
            TestAgent.RegisterCommand("window", _ =>
                $"visible={UI.SessionWindow.Visible} failed={UI.SessionWindow.DrawFailed} key={UI.WindowKey.Bound}");
            TestAgent.RegisterCommand("windowpage", page => UI.SessionWindow.Open(page.Trim()));
            // Spilled pickups within 300 of the player, "count name,name", and one name by index for use.
            TestAgent.RegisterCommand("spill", _ =>
            {
                List<string> lying = SpilledNearby();
                return lying.Count + " " + string.Join(",", lying);
            });
            TestAgent.RegisterCommand("spillname", index => SpilledNearby()[int.Parse(index.Trim())]);
            TestAgent.RegisterCommand("elevator", _ => WorldObjects.DescribeElevators());
            TestAgent.RegisterCommand("door", WorldObjects.DescribeDoors);
            TestAgent.RegisterCommand("doorunlock", _ => WorldObjects.UnlockNearestDoor());
            TestAgent.RegisterCommand("lockstate", range => WorldObjects.DescribeLocks(range.Length > 0 ? float.Parse(range, System.Globalization.CultureInfo.InvariantCulture) : 300f));
            TestAgent.RegisterCommand("combatmusic", _ =>
                AudioGameHandler.TryGet(out AudioGameHandler audio) && audio?.GetMusicModule()?.TryCast<MusicModule>() is { } music
                    ? (music._isPlayingCombatMusic ? "1" : "0")
                    : "no music");
            // Loaded locked things a lockpick opens, nearest first, as "parent/name@x,y talent=<needed talent>".
            TestAgent.RegisterCommand("locks", _ =>
            {
                if (!PlayerAccess.TryGetPlayer(out Actor player)) return "no player";
                Vector2 at = player.transform.position;
                string text = string.Join(";", UnityEngine.Object.FindObjectsOfType<Il2CppDrova.InteractionSystem.Interact_Condition_Locked>()
                    .Where(locked => locked.CanLockpick && locked._depot != null && locked._depot.IsLocked())
                    .OrderBy(locked => Vector2.Distance(at, locked.transform.position))
                    .Take(8)
                    .Select(locked => $"{(locked.transform.parent != null ? locked.transform.parent.name + "/" : "")}{locked.name}@{locked.transform.position.x:0},{locked.transform.position.y:0} talent={(locked._requiredLockpickingTalent != null ? locked._requiredLockpickingTalent.name : "none")} key={WorldObjects.KeyOf(locked) ?? "none"} depot={DepotPath(locked._depot)}"));
                return text.Length > 0 ? text : "none";
            });
            // Presses the nearest elevator panel past its quest condition, through the hook a real press goes through.
            TestAgent.RegisterCommand("elevatorpress", _ =>
            {
                if (!PlayerAccess.TryGetPlayer(out Actor player)) throw new InvalidOperationException("no player yet");
                Il2CppDrova.InteractionSystem.Interact_Bhvr_ElevatorPanel? panel = UnityEngine.Object.FindObjectsOfType<Il2CppDrova.InteractionSystem.Interact_Bhvr_ElevatorPanel>()
                    .OrderBy(candidate => ((Vector2)candidate.transform.position - player.GetFeetPosition()).sqrMagnitude).FirstOrDefault()
                    ?? throw new InvalidOperationException("no elevator panel loaded");
                panel.BeginInteraction(new Il2CppDrova.InteractionSystem.InteractArgs { Actor = player });
                return "pressed " + panel.name;
            });
            TestAgent.RegisterCommand("lootlist", _ => LootShare.OpenContents());
            TestAgent.RegisterCommand("loottake", id => LootShare.TakeFromOpen(id.Trim()));
            TestAgent.RegisterCommand("seed", _ => Drova_Modding_API.Access.CoopAccess.SessionSeed.ToString());
            TestAgent.RegisterCommand("steered", guid =>
                EntityGameHandler.TryGet(out EntityGameHandler handler)
                    && handler.GetActorFromGuid(guid.Trim()) is { } actor && RemoteEnemies.IsSteered(actor) ? "1" : "0");
            // The session id the host gave this creature here, or none yet. A blow before it has one is never reported.
            TestAgent.RegisterCommand("netid", guid =>
                EntityGameHandler.TryGet(out EntityGameHandler handler)
                    && handler.GetActorFromGuid(guid.Trim()) is { } actor && EnemyRegistry.TryGetId(actor, out EnemyNetId id) ? id.Value.ToString() : "none");
            // poise <me|mate|guid|name> [break|drain]. Whether that body's poise is broken here. Break forces it, as a guard break does.
            TestAgent.RegisterCommand("poise", args =>
            {
                string[] parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (PoiseTarget(parts[0]) is not { PoiseController: { } poise }) throw new InvalidOperationException("no poise for " + parts[0]);
                if (parts.Length > 1 && parts[1] == "break") PuppetPoise.Break(poise);
                // Unforced, as a landing blow's poise damage is.
                if (parts.Length > 1 && parts[1] == "drain") poise.ChangePoise(0f - Mathf.Max(poise.CurValue, 1f));
                string behaviors = string.Join(",", poise.GetComponentsInChildren<Il2CppDrova.Poise.APoiseBhvr>(true)
                    .Select(behavior => behavior.GetIl2CppType().Name + (behavior.enabled && behavior.gameObject.activeInHierarchy ? "" : "(off)")));
                return "broken=" + (poise.TryGetPoiseBreak(out _) ? "1" : "0") + $" cur={poise.CurValue:0} max={poise.MaxValue:0} owner={(poise.Owner == null ? "-" : poise.Owner.name)} puppet={(RemotePlayers.IsPuppet(poise.Owner) ? 1 : 0)} bhvrs={behaviors}";
            });
            // This machine's HUD dirt, the mole's blind. State Idle=0 and alpha 0 means clean.
            TestAgent.RegisterCommand("dirt", _ =>
            {
                if (!GUIGameHandler.TryGet(out GUIGameHandler gui) || gui?._guiHudInstance?._dirtEffect is not { } dirt) throw new InvalidOperationException("no hud yet");
                return $"state={(int)dirt._state} alpha={(dirt._canvas == null ? 0f : dirt._canvas.alpha):0.00}";
            });
            // freeze <guid>. A body's hit freeze frame and the visibility gates that tick it.
            TestAgent.RegisterCommand("freeze", guid =>
            {
                if (!EntityGameHandler.TryGet(out EntityGameHandler handler) || handler.GetActorFromGuid(guid.Trim()) is not { } actor) throw new InvalidOperationException("no actor " + guid);
                Actor.AnimatorModule animator = actor.GetAnimatorModule();
                return $"freeze={animator._freezeFrameDuration:0.00} in={(animator.IsInFreezeFrame ? 1 : 0)} animvis={(animator._isAnimatorVisible ? 1 : 0)} "
                    + $"visible={(actor._visibleCallback != null && actor._visibleCallback.IsVisible ? 1 : 0)} hasvis={(actor._hasVisibleCallback ? 1 : 0)} "
                    + $"action={(actor.GetActionModule()?._currentAction == null ? "-" : actor.GetActionModule()._currentAction.name)}";
            });
            // lazyhit <guid|mate>. That body's hit receivers and live hit boxes, and whether the lazy loader has them on.
            TestAgent.RegisterCommand("lazyhit", target =>
            {
                Actor? actor = target.Trim() == "mate"
                    ? RemotePlayers.Bodies.Values.FirstOrDefault(body => body?.Body != null)?.Body
                    : EntityGameHandler.TryGet(out EntityGameHandler handler) ? handler.GetActorFromGuid(target.Trim()) : null;
                if (actor == null) throw new InvalidOperationException("no actor " + target);
                IEnumerable<string> receivers = actor.GetComponentsInChildren<Il2CppDrova.HitSystem.HitReceiveHandler>(true)
                    .Select(receiver => $"R:{receiver.name}={(receiver.IsLoaded ? 1 : 0)}{(receiver._simulatAlways ? "a" : "")}");
                IEnumerable<string> boxes = UnityEngine.Object.FindObjectsOfType<Il2CppDrova.HitSystem.HitTriggerHandler>()
                    .Where(box => box._hasOwner && box._owner != null && box._owner.Pointer == actor.Pointer)
                    .Select(box => $"T:{box.name}={(box.IsLoaded ? 1 : 0)}{(box._ignoreLazySystem ? "i" : "")}");
                return string.Join(" ", receivers.Concat(boxes));
            });
            TestAgent.RegisterCommand("teammate", _ =>
            {
                if (!PlayerAccess.TryGetPlayer(out Actor self)) throw new InvalidOperationException("no player yet");

                float nearest = float.MaxValue;
                foreach (Vector2 position in RemotePlayers.Positions)
                {
                    nearest = Math.Min(nearest, (position - self.GetFeetPosition()).magnitude);
                }

                // Also the reported spot, since a body outside the loaded area can lag behind it.
                float reported = float.MaxValue;
                foreach (Messages.PlayerStateMessage state in CoopSession.LatestStates.Values)
                {
                    reported = Math.Min(reported, (new Vector2(state.X, state.Y) - self.GetFeetPosition()).magnitude);
                }

                return nearest == float.MaxValue ? "none" : "dist=" + nearest.ToString("0", System.Globalization.CultureInfo.InvariantCulture) +
                    " reported=" + (reported == float.MaxValue ? "-" : reported.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
            });
            // Plain numbers for assertions, so a scenario need not parse coop_status prose.
            Dictionary<string, Func<int>> counters = new()
            {
                ["held"] = () => StorySceneLease.Held,
                ["modsetdiffers"] = () => ModSet.Mismatches,
                ["followed"] = () => GateFollow.Followed,
                ["storyports"] = () => GateFollow.StoryPortsReported,
                ["reach"] = () => AnyPlayerReach.Answered,
                ["regressions"] = () => RemoteGVar.RegressionsRefused,
                ["mirrored"] = () => GVarDerived.MirroredSkipped,
                ["handedin"] = () => ItemGrantShare.HandedIn,
                ["grantsreceived"] = () => ItemGrantShare.Received,
                ["grantsunresolved"] = () => ItemGrantShare.Unresolved,
                ["rewardsunresolved"] = () => RewardShare.Unresolved,
                ["xpsent"] = () => ExperienceShare.Sent,
                ["xpreceived"] = () => ExperienceShare.Received,
                ["credited"] = () => RemoteEnemyDeath.Credited,
                ["bedmoved"] = () => MineDayLoop.Moved,
                ["worked"] = () => ResourceWork.Worked,
                ["workapplied"] = () => ResourceWork.Applied,
                ["leases"] = () => EnemyEngagements.Leases,
                ["assists"] = () => TeamBrawls.Assists,
                ["brawljoined"] = () => TeamBrawls.Joined,
                ["leasehealth"] = () => RemoteEnemyHealth.LeasedHealthTaken,
                ["dmgsent"] = () => DamageWatch.ReportedPlayerBlows,
                ["dmgapplied"] = () => RemoteDamage.Applied,
                ["dodged"] = () => RemoteDamage.Dodged,
                ["copyhits"] = () => DamageWatch.CopyBlowsOnLocal,
                ["discarded"] = () => RemoteEnemyBlows.Discarded,
                ["creaturehits"] = () => RemoteEnemyBlows.Landed,
                ["kepttrigger"] = () => CameraReach.KeptTrigger,
                ["keptreceiver"] = () => CameraReach.KeptReceiver,
                ["blockbroken"] = () => BlockVerdicts.Sent,
                ["blockshown"] = () => BlockVerdicts.Applied,
                ["perfectwindow"] = () => RemotePlayer.PerfectWindows,
                ["summonshared"] = () => SummonWatch.Shared,
                ["summonpaired"] = () => SummonWatch.Paired,
                ["morphspared"] = () => MorphFriendlyFire.Refused,
                ["cutports"] = () => CutscenePorts.Applied,
                ["keptplaying"] = () => RemoteEnemy.KeptPlaying,
                ["idlecleared"] = () => RemoteEnemy.IdleCleared,
                ["cutshort"] = () => RemoteEnemy.CutShort,
                ["shoved"] = () => RemoteEnemy.Shoved,
                ["snaps"] = () => RemoteEnemy.Snaps,
                ["swingsdropped"] = () => RemoteEnemy.SwingsDropped,
                ["aimed"] = () => RemoteEnemy.AbilitiesAimed,
                ["echoignored"] = () => RemoteGVar.EchoesIgnored,
                ["standretry"] = () => DownedState.StandRetries,
                ["lootsent"] = () => LootShare.Sent,
                ["lootapplied"] = () => LootShare.Applied,
                ["chestoffered"] = () => LootShare.Proposed,
                ["bodiesnamed"] = () => LootShare.BodiesNamed,
                ["cachesent"] = () => CacheShare.Sent,
                ["cacheapplied"] = () => CacheShare.Applied,
                ["scaled"] = () => EnemyScaling.Scaled,
                ["backups"] = () => SaveBackups.Made,
                ["savemarks"] = () => CoopSaveMarks.Marked,
                ["playedalone"] = () => CoopSaveMarks.Warnings,
                ["choicesclosed"] = () => SharedChoices.Closed,
                ["gameovers"] = () => GameOver.Count,
                ["spawnershared"] = () => SpawnerShare.Shared,
                ["spawnerlefttohost"] = () => SpawnerShare.LeftToHost,
                ["spawnerresumed"] = () => SpawnerShare.Resumed,
                ["stunned"] = () => RemoteEnemy.Stunned,
                ["puppetstunned"] = () => RemotePlayer.Stunned,
                ["checkpoint"] = () => Checkpoint.Committed,
                ["worldsent"] = () => WorldObjects.Sent,
                ["worldapplied"] = () => WorldObjects.Applied,
                ["spritesent"] = () => WorldObjects.SpritesSent,
                ["tradedone"] = () => Trade.Completed,
                ["traderefused"] = () => Trade.Refused,
                ["menuchosen"] = () => TeammateMenu.Chosen,
                ["invitesaccepted"] = () => Invites.Accepted,
                ["fogletwoken"] = () => FogletShare.Woken,
                ["bledout"] = () => DownedState.BledOut,
                ["helpedup"] = () => DownedState.HelpedUp,
                ["dirtrelayed"] = () => PuppetDirtBlind.Relayed,
                ["dirtshown"] = () => PuppetDirtBlind.Shown,
                ["keptanimating"] = () => EnemyPinning.KeptAnimating,
                ["statusended"] = () => StatusWatch.EndedApplied,
                ["linessent"] = () => SpokenLines.Sent,
                ["linesshown"] = () => SpokenLines.Shown,
                ["bodylines"] = () => SpokenLines.BodyShown,
                ["voiced"] = () => SpokenLines.Voiced,
                ["boolops"] = () => GVarDerived.BoolOperations,
                ["gintdup"] = () => GVarDerived.OperationsSkipped,
                ["spawnshared"] = () => FogletMinions.Shared,
                ["spawnsannounced"] = () => SpawnedEnemies.AnnouncedCount,
                ["spawnsbuilt"] = () => SpawnedEnemies.BuiltCount,
                ["spawnskipped"] = () => FogletMinions.Skipped,
                ["conjurecast"] = () => FogletMinions.Cast,
                ["traplaid"] = () => TrapShare.Laid,
                ["summonsbuilt"] = () => LeaseSummons.Built,
                ["thundersent"] = () => WeatherSync.ThunderSent,
                ["guardwitness"] = () => GuardReach.Witnessed,
                ["volumeentered"] = () => GuardReach.Entered,
                ["thundershown"] = () => WeatherSync.ThunderShown,
                ["pilesremoved"] = () => HaulShare.PilesRemoved,
                ["pileslate"] = () => HaulShare.LateSettled,
                ["regionstaken"] = () => RegionShare.Taken,
                ["arenajoined"] = () => ArenaTeammates.Joined,
                ["arenako"] = () => ArenaTeammates.KnockedOut,
                ["brawlblows"] = () => RemoteDamage.BrawlBlows,
                ["storyclaimed"] = () => StoryClaims.Claimed,
                ["storyhidden"] = () => StoryClaims.Hidden,
                ["summonsbound"] = () => LeaseSummons.Bound,
                ["trapshown"] = () => TrapShare.Shown,
                ["trapwithdrawn"] = () => TrapShare.Withdrawn,
                ["gatesapplied"] = () => ArenaGates.Applied,
                ["leasemirrored"] = () => LeaseMirror.Mirrored,
                ["arenarefunded"] = () => Arena.Refunded,
                ["copyeffects"] = () => StatusWatch.CopyEffectsRefused,
                ["creaturesent"] = () => DamageWatch.CreatureBlowsSent,
                ["creatureapplied"] = () => RemoteEnemyHealth.CreatureBlowsApplied,
                ["hostcopyfights"] = () => DamageWatch.HostCopyFights,
            };
            TestAgent.RegisterCommand("counter", name => counters.TryGetValue(name.Trim().ToLowerInvariant(), out Func<int> read)
                ? read().ToString()
                : throw new InvalidOperationException("counters: " + string.Join(", ", counters.Keys)));
            TestAgent.RegisterState(() => $"session={CoopSession.State} direct={CoopSession.DirectPort} " +
                $"players={CoopSession.Players.Count} ff={(CoopSession.FriendlyFire ? "on" : "off")}");
        }
    }
}
#endif
