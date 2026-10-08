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
        internal static void Register()
        {
            TestAgent.RegisterCommand("host", _ => CoopActions.Host());
            // The host's loot rule, set before hosting.
            TestAgent.RegisterCommand("lootmode", mode =>
            {
                CoopPreferences.SharedLoot = mode.Trim().Equals("shared", StringComparison.OrdinalIgnoreCase);
                return CoopPreferences.SharedLoot ? "shared" : "instanced";
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
            // Sets the loaded savegame's game mode, so a Hardcore run can be tested on a throwaway hero.
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
            TestAgent.RegisterCommand("leave", _ => CoopActions.Leave());
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
            TestAgent.RegisterState(() => $"session={CoopSession.State} code={CoopSession.SessionCode ?? "-"} " +
                $"players={CoopSession.Players.Count} ff={(CoopSession.FriendlyFire ? "on" : "off")}");
        }
    }
}
