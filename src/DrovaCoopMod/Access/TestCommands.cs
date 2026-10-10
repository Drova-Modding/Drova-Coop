using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Testing;
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
    /// <summary>
    /// The test agent's commands for this mod. Inert unless the game runs with --agent-port.
    /// </summary>
    internal static class TestCommands
    {
        private static List<string> SpilledNearby() => PlayerAccess.TryGetPlayer(out Actor player)
            ? CacheShare.Lying(player.GetFeetPosition(), 300f)
            : throw new InvalidOperationException("no player yet");

        internal static void Register()
        {
            TestAgent.RegisterCommand("host", _ => CoopActions.Host());
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
            // Reads a shared host setting. With a value, enforces it here, which on the host sets the session's value.
            TestAgent.RegisterCommand("hostsetting", args =>
            {
                string[] parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                Drova_Modding_API.Systems.Coop.HostSetting setting = CoopAccess.HostSettings.FirstOrDefault(s => s.Key == parts[0])
                    ?? throw new InvalidOperationException("no shared setting " + parts[0]);
                if (parts.Length > 1) setting.Enforce(parts[1]);
                return setting.Read();
            });
            TestAgent.RegisterCommand("join", CoopActions.Join);
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
            // Plain numbers for assertions: "counter held >= 1" rather than parsing coop_status prose.
            Dictionary<string, Func<int>> counters = new()
            {
                ["held"] = () => StorySceneLease.Held,
                ["followed"] = () => GateFollow.Followed,
                ["storyports"] = () => GateFollow.StoryPortsReported,
                ["reach"] = () => AnyPlayerReach.Answered,
                ["regressions"] = () => RemoteGVar.RegressionsRefused,
                ["mirrored"] = () => GVarDerived.MirroredSkipped,
                ["handedin"] = () => ItemGrantShare.HandedIn,
                ["credited"] = () => RemoteEnemyDeath.Credited,
                ["bedmoved"] = () => MineDayLoop.Moved,
                ["worked"] = () => ResourceWork.Worked,
                ["workapplied"] = () => ResourceWork.Applied,
                ["leases"] = () => EnemyEngagements.Leases,
                ["dmgsent"] = () => DamageWatch.ReportedPlayerBlows,
                ["dmgapplied"] = () => RemoteDamage.Applied,
                ["copyhits"] = () => DamageWatch.CopyBlowsOnLocal,
                ["blockbroken"] = () => BlockVerdicts.Sent,
                ["blockshown"] = () => BlockVerdicts.Applied,
                ["summonshared"] = () => SummonWatch.Shared,
                ["summonpaired"] = () => SummonWatch.Paired,
                ["cutports"] = () => CutscenePorts.Applied,
                ["keptplaying"] = () => RemoteEnemy.KeptPlaying,
                ["idlecleared"] = () => RemoteEnemy.IdleCleared,
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
                ["choicesclosed"] = () => SharedChoices.Closed,
                ["gameovers"] = () => GameOver.Count,
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
                ["summonsbound"] = () => LeaseSummons.Bound,
                ["trapshown"] = () => TrapShare.Shown,
                ["trapwithdrawn"] = () => TrapShare.Withdrawn,
                ["gatesapplied"] = () => ArenaGates.Applied,
                ["leasemirrored"] = () => LeaseMirror.Mirrored,
                ["arenarefunded"] = () => Arena.Refunded,
                ["copyeffects"] = () => StatusWatch.CopyEffectsRefused,
            };
            TestAgent.RegisterCommand("counter", name => counters.TryGetValue(name.Trim().ToLowerInvariant(), out Func<int> read)
                ? read().ToString()
                : throw new InvalidOperationException("counters: " + string.Join(", ", counters.Keys)));
            TestAgent.RegisterState(() => $"session={CoopSession.State} code={CoopSession.SessionCode ?? "-"} direct={CoopSession.DirectPort} " +
                $"players={CoopSession.Players.Count} ff={(CoopSession.FriendlyFire ? "on" : "off")}");
        }
    }
}
