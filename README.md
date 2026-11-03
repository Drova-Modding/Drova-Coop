# Drova Coop

A co-op mod for Drova - Forsaken Kin, built on the
[Drova Modding API](https://github.com/Drova-Modding/Drova-Modding-API) networking transport.

What changed in each version is in [CHANGELOG.md](CHANGELOG.md). Thanks to the people in [CREDITS.md](CREDITS.md).

## Requirements

- The Modding API 0.8.0 or later: `Mods/Drova_Modding_API.dll` and `UserLibs/LiteNetLib.dll`, both in
  `Modding_API.zip` on the [API releases](https://github.com/Drova-Modding/Drova-Modding-API/releases).
  With an older API build without networking the mod stays idle and says why.
- For online play, the Steam copy of the game, or a forwarded UDP port on the host. On one local network
  neither is needed.
- All players on the same mod version. A mismatch is refused when joining.
- All players with the same Modding API content files under `Mods/Modding_API` (quests, placements, items,
  map layouts, dialogues). A difference is reported when joining.
- All players in the same game mode (Classic, Story, Immersive, Hardcore, Insane). A client whose save is in
  another mode is sent back with the reason.
- World-changing mod settings (for example Insane Monsters) follow the host while the session runs.
- Up to 16 players, host included. Two is the tested setup.

## Host and join

Press F8 (rebind it in the game's options, Modding tab, under Drova Coop) or run `coop_window`. Everything a
player needs is in this window. The commands below do the same from the console.

- **Host a game**: pick "Steam friends" (friends join from Steam or your invite) or "Network or address"
  (friends find the game in their list, or join by your address).
- **Join a game**: Steam friends hosting and games on your network are listed by themselves. Click one. Otherwise
  type the host's IP or IP:port under "Join by address" and press Join.
- **In a session** the window shows how friends join and three tabs:
  - Players: everyone with their ping, Trade, and Go to for teleporting to a teammate. A client also has Catch up,
    which fetches the host's quest and world state again.
  - Arena: your team. The host marks the ring (size, Mark ring here, Remove ring), then starts the fight with a
    number of rounds or none, and can stop it.
  - Rules: the host changes them here, and everyone else sees them.
- **Save backups**: every save that has copies, newest first, by hero name. Restore one from the main menu.
  It asks first, and the save as it is now is kept as a copy too.

Escape closes the window. Gameplay keys are off while it is open. Your hero's name is your name in the
session.

### Steam friends

Hosting "Steam friends" needs the Steam copy of the game. Friends join by "Join Game" on you in Steam, by your
invite (the Invite button in the window), or from the window's "Steam friends hosting" list. A join started from
Steam, also one that starts the game, waits until the friend has loaded a save. Only the host's Steam friends get
in. Traffic goes through Steam's relay network, so no port needs forwarding and nobody sees the other's address.
"Direct connection" (More options when hosting, or under the Steam friends list when joining, and both players need it
on) lets Steam try a direct path first, which can lower the ping. Caution: the game freezes for about 2 seconds when
someone joins, and you see each other's IP address.

### Local network and direct play

Hosting "Network or address" listens on a UDP port (`LanPort`, default 9061). Friends on the network see
the game in their list. For internet play without Steam, forward that port and have friends join by address.

- A direct session has no password and is not encrypted. Anyone who can reach the port can join, so use Steam
  friends for a closed session over the internet.
- The first time you host directly, Windows asks whether Drova may use the network. Allow private networks,
  or nobody can join.

## Commands

| Command | Does |
|---|---|
| `coop_window` | Open or close the session window. |
| `coop_host_lan [port]` | Host on the local network or a forwarded port. |
| `coop_join_direct <ip[:port]>` | Join a direct host by address. |
| `coop_host_steam` | Host for your Steam friends. |
| `coop_join_steam <steamid64>` | Join a Steam friend's game by their Steam id. |
| `coop_invite` | Open the Steam invite dialog while hosting over Steam. |
| `coop_lan` | List games found on the local network. |
| `coop_leave` | Leave the session. |
| `coop_status` | Session state, your id, the address, encryption, players and ping, version check result. |
| `coop_tp [player]` | Teleport to a teammate by name or id. Without one, to the only other player. |
| `coop_trade [name]` | Trade with the nearest teammate, or the named one. |
| `coop_team [n\|off]` | Pick your arena team (1-250). |
| `coop_arena [radius\|start [rounds]\|stop\|off]` | Host only. Mark a PvP circle around you (radius 2-2000, default 1000). `start` begins a fight in it, optionally stopping after a number of rounds. `stop` ends it, `off` removes the ring. |
| `coop_checkpoint` | Host only. Send everyone back to the party checkpoint. |
| `coop_revive [range]` | Host only. Revive dead creatures nearby, in world and save (default 300 units). |
| `coop_resync [deep]` | Fetch quest state and journal from the host again. `deep` also re-runs their effects. |
| `coop_saves` | List save slots a session has written into. |
| `coop_backups` | List save backups and where they are. |
| `coop_restore <hero> <slot> [latest\|session\|name]` | Main menu only. Restore a backup. The current slot is copied first. |

Diagnostics for bug reports: `coop_gvars`, `coop_gvar_trace`, `coop_gvar_quiet`, `coop_gvar_derived`,
`coop_work`, `coop_haul`, `coop_clock`, and `coop_guids` (debug builds). Each prints its usage in the console.

## Settings

`UserData/MelonPreferences.cfg`, section `[DrovaCoop]`:

| Setting | Default | Meaning |
|---|---|---|
| `LanPort` | 9061 | UDP port for direct hosting and for finding games on the local network. |
| `FriendlyFire` | false | Session rule. |
| `SharedLoot` | false | Session rule. |
| `ArenaMode` | false | Host in arena mode (see below). Taken when hosting starts. |
| `StandNearRevive` | false | Session rule: standing over a downed teammate for a moment gets them up, besides the hold prompt. |
| `HostLeadsStory` | false | Session rule: only the host starts quest dialogues. The others do not see those dialogue options. |
| `RecoveryHealth` | 5 | Session rule: percent of health a downed player gets up with (1-100). |
| `EnemyHealth` | 100 | Session rule: enemy health in percent (50-300). Does not change the save. |
| `EnemyDamage` | 100 | Session rule: how hard enemies hit players, in percent (50-300). |
| `SaveBackups` | 10 | Backups kept per slot. 0 turns them off. |
| `VersionCheckUrl` | this repo's `VERSION` | Empty turns the version check off. |

Session rules are set in the window's Rules tab or before hosting. Only the host's values count.

## What is shared

- Players: body, movement, animation, equipment, attacks, blocks, spells, eating, mining and fishing.
- Morph spells (Modding API): teammates see a morphed player as that creature, moving and attacking.
  When the creature dies, the player goes down like from any lethal blow.
- Creatures: the host simulates them. Blows, deaths, revives and knockouts reach everyone.
- Status effects and their cures.
- Quests, journal, map unlocks, experience, knowledge and dialogue item grants.
- Story cutscene NPC moves, NPC speech and teammates' dialogue answers (as speech bubbles).
- Day and night follow the host, including the host's sleep. Time keeps running while a player is in a menu.
- Doors, levers, gates, crates and pickups. They are written into the host's save, so a later joiner is
  caught up.
- Loot is per player by default. With shared loot, a pickup taken is gone for everyone and a chest holds
  what the last player left. Chests you store things in stay your own.

## Downed, game over and checkpoint

- A downed player gets up after a while, or at once when a teammate holds the interact key on them (the hand
  prompt over their body). With the host's "Revive by standing near" on, standing over them for a moment works too.
- When every player is down at once, or one dies, the run ends and everyone goes back to the party
  checkpoint. Without one yet, each goes back to their own latest save.
- Losing a plain brawl or anything in the arena never ends the run.
- The checkpoint is written whenever anyone saves, into party slots 90-99 in rotation, but only when all
  players can save.
- When the host loads a save from this session or a party slot, everyone goes back with it.
- Go back by choice with `coop_checkpoint` or the pause-menu button, where the game mode allows it.
- Immersive: the checkpoint is a shared autosave, and a lost run loads the newest autosave.
- Hardcore: no checkpoint. A downed player bleeds out after 30 seconds unless a teammate helps them
  up. Bleeding out ends the run with the game's death screen and saves turn Iron. A teammate's
  blow never starts a bleed-out.

## Arena and PvP

- Friendly fire is off by default.
- The host marks a circle on the ground, then starts a fight in it: there players can hurt each other anyway.
  Arena in the session window, or `coop_arena`. Marking alone changes nothing.
- A fight can have a number of rounds. After the last one it stops by itself and the score is read out.
  The ring stays for the next fight.
- Players fight as teams or alone. Pick your team with the arrows in the window, `coop_team`, or a teammate's
  invite. Teammates cannot hurt each other.
- The last side standing wins the round. Everyone stands up healed and is safe for 3 seconds.
  Rounds won are shown in `coop_status`.
- Hits land one network round trip late, so fights are not perfectly fair.
- Consumables and ammunition used in a fight in the ring come back when it ends.
- In Morvin's arena, a teammate who strikes joins the fight on the player's side.

### Arena mode

For players who only want to fight each other, for example from saves in different factions. The host picks
"Arena only" before hosting (or sets `ArenaMode`).

- Each player keeps their own world: quests, factions, journal, loot, world objects, creatures, time and
  weather stay per machine, and nothing is written into anyone else's save.
- Shared: player bodies, blows, blocks, status effects, knockdowns, summons, the arena and duels, trade.
- The host may load any of its saves without ending the session.
- Monsters of one world never hurt a player standing in another's.

## Talking and trading

- Walk up to a teammate and press interact to trade, challenge to a duel or invite to your arena
  team. The other player must accept. The game offers no talk to an opponent: inside a marked ring on
  another team, or with friendly fire on. Trade from the session window's Players tab then.
- Trade opens the game's merchant window with the teammate's bag, minus what they wear. Quest items
  can be traded. Press Trade, and the teammate accepts or refuses. Values need not balance, so gifts
  work.

## Limitations

- No lobby, friends list or invites. Share the code yourselves.
- The host is not fully authoritative. Do not play with people you do not trust.
- Under shared loot, two players taking from one chest stack at the same moment can both keep their part.
- Random rolls (extra projectiles, crits) and a crate's spilled pickups differ per machine. Only the
  outcome of a hit is shared.
- NPCs from the API's NPC placement files are placed by each player's own copy of the mod. Where they walk
  and what they say is not kept in step. Creatures from those files, custom quests' journal entries and map
  layout traps and blockers are shared.
- A client's bed and fast travel cost no time.
- In an escort sequence only the host gets the conversation window.

## Saves and backups

- Back up your saves yourself too. What a session writes into a save cannot be undone.
- Before the game overwrites a slot, the mod copies it to `UserData/CoopBackups/Hero_NN/Savegame_NN/`:
  `recent_*` keeps the last `SaveBackups` (10) saves, `session_*` the first save of each of the last 3
  sessions. Restore from the main menu (session window or `coop_restore`).
- `coop_saves` lists slots a session wrote into, and the mod warns when you play one alone.
- At start the mod reads this repo's `VERSION` file. A build listed as bad cannot host or join. No network
  means allowed.

## Troubleshooting

- Mod does nothing: install Modding API 0.8.0 or later, including `UserLibs/LiteNetLib.dll`. The log says why.
- Cannot join: check the address and port, the mod version, and the host's firewall.
- Quest out of sync: run `coop_resync`, or `coop_resync deep`.
- Something not syncing: run `coop_status` and include its output in the bug report.
- Compare two saves: `dotnet run --project tools/DrovaCoopMod.SaveDiff -- <baseline> <candidate>` lists
  what the second holds that the first does not. Arguments are `data.sav` files or `Savegame_NN` folders
  under `%USERPROFILE%\AppData\LocalLow\Just2D\Drova`.

## Build

```sh
dotnet build src/DrovaCoopMod
dotnet test tests/DrovaCoopMod.Protocol.Tests
git config core.hooksPath .githooks
```

The mod is copied to the game's `Mods` folder, the protocol DLL to `UserLibs`. Override the game
folder with `-p:GameDir="D:\Games\Drova - Forsaken Kin"`. Without the game installed, put
`Drova_Modding_API.dll` in `.api/` or pass `-p:ApiAssembly=<path>`.

A second player without a second game:

```sh
dotnet run --project tools/DrovaCoopMod.HeadlessPeer -- --address 127.0.0.1 --port 9061 --mirror --name Mirror
```

`--mirror` copies your movement. `--move` walks a circle.
