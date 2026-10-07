# Drova Coop

A co-op mod for Drova - Forsaken Kin, built on the
[Drova Modding API](https://github.com/Drova-Modding/Drova-Modding-API) networking transport.

## Requirements

- A coop build of the Modding API: `Mods/Drova_Modding_API.dll` and `UserLibs/LiteNetLib.dll`.
  Build it with `dotnet build "Drova Modding API.sln" -c ReleaseCoop`, or take `Modding_API_Coop.zip`
  from the [API releases](https://github.com/Drova-Modding/Drova-Modding-API/releases).
  With a plain API build the mod stays idle and says why.
- A relay, unless both players can reach each other directly. See
  [Drova-Coop-Relay](https://github.com/Drova-Modding/Drova-Coop-Relay).
- All players on the same mod version. A mismatch is refused when joining.
- All players in the same game mode (Classic, Story, Immersive, Hardcore, Insane). It decides spawns, saving and
  dying, so a client whose save is in another mode leaves with the reason.
- Mods that change the world (for example Insane Monsters in Classic) follow the host's settings while the session
  runs; a client already in a world loads its save once to pick them up. Their own settings come back on leave.
- Up to 16 players, host included. Two is the tested setup.

## Host and join

Press F8 or run `coop_window`. Choose Host or Join first. Host asks for an optional code, a password
and the session rules; Join asks only for the code and password (Paste code fills it from the
clipboard). While a session runs it shows the code to hand on (with Copy), whether the relay can
read the traffic, and each player with their ping. Escape closes it. Gameplay keys are off while it
is open.

To host, leave the code empty and press Host. The relay picks a code. Send it to your friends
outside the game. They paste it and press Join.

## Commands

| Command | Does |
|---|---|
| `coop_window` | Open or close the session window. |
| `coop_host` | Host through the configured relay. |
| `coop_join [code]` | Join. Without a code, uses the configured one. |
| `coop_leave` | Leave the session. |
| `coop_status` | Session state, your id, the code, encryption, players and ping, version check result. |
| `coop_tp [player]` | Teleport to a teammate by name or id. Without one, to the only other player. |
| `coop_trade [name]` | Trade with the nearest teammate, or the named one. |
| `coop_team [n\|off]` | Pick your arena team (1-250). |
| `coop_arena [radius\|off]` | Host only. Mark a PvP circle around you (radius 2-2000, default 2000). |
| `coop_checkpoint` | Host only. Send everyone back to the party checkpoint. |
| `coop_revive [range]` | Host only. Revive dead creatures nearby, in world and save (default 300 units). |
| `coop_resync [deep]` | Fetch quest state and journal from the host again. `deep` also re-runs their effects. |
| `coop_saves` | List save slots a session has written into. |
| `coop_backups` | List save backups and where they are. |
| `coop_restore <hero> <slot> [latest\|session\|name]` | Main menu only. Restore a backup. The current slot is copied first. |

Diagnostics:

| Command | Does |
|---|---|
| `coop_gvars [filter]` | List global variables that differ from their initial value. |
| `coop_gvar_trace [filter]` | Log variables matching the filter. No argument stops it. |
| `coop_gvar_quiet [on\|off]` | Stop reacting to variables from teammates. Values still arrive. |
| `coop_gvar_derived` | List variables this world keeps as its own tally. |
| `coop_work [on\|off]` | Shared mining status. `off` stops replaying teammates' swings. |
| `coop_haul [on\|off\|repair\|ping]` | Shared rubble hauling status and repair. |
| `coop_clock [on\|off]` | Whether this world may advance its own day. |
| `coop_guids [range]` | Debug builds only. Creatures that cannot be found by their guid. |

## Settings

`UserData/MelonPreferences.cfg`, section `[DrovaCoop]`:

| Setting | Default | Meaning |
|---|---|---|
| `PlayerName` | | Your name in the session. |
| `RelayAddress`, `RelayPort` | port 9060 | The relay. |
| `SessionCode` | empty | Code to join. Empty when hosting lets the relay pick one. |
| `SessionPassword` | | Also the encryption key. Empty means the relay can read and forge traffic. |
| `WindowKey` | `<Keyboard>/f8` | Opens the session window. Empty turns the key off. |
| `FriendlyFire` | false | Session rule. |
| `SharedLoot` | false | Session rule. |
| `RecoveryHealth` | 5 | Session rule: health a downed player gets up with. |
| `EnemyHealth` | 100 | Session rule: enemy health in percent. Does not change the save. |
| `SaveBackups` | 10 | Backups kept per slot. 0 turns them off. |
| `VersionCheckUrl` | this repo's `VERSION` | Empty turns the version check off. |

Session rules are set in the window. Only the host's count.

## What is shared

- Player bodies, movement, animation, build, voice, equipment and stance.
- Attacks, combos, power attacks, blocks, spells, drinking, eating, mining, fishing and slings.
- Creatures. The host simulates them. Blows, deaths, revives and knockouts cross.
- Status effects and their cures.
- Quests, journal, map unlocks, experience, knowledge, dialogue item grants and the mine's rubble haul.
- NPC moves and walks from story cutscenes, and NPC duels started by one player's dialogue.
- NPC speech and teammates' dialogue answers as speech bubbles, voiced lines included.
- Day and night follow the host, including the host's sleep.
- Doors, levers, gates, crates and pickups. They are written into the host's save, so a later joiner
  is caught up.
- The world keeps streaming around every player.
- Time keeps running when one player opens a menu.
- Loot is per player by default. With shared loot, a pickup taken is gone for everyone and a chest
  holds what the last player left. Chests you store things in stay your own.

## Downed, game over and checkpoint

- A downed player gets up after a while.
- When every player is down at once, or one dies, the run ends and everyone goes back to the party
  checkpoint. Without one yet, each goes back to their own latest save.
- Losing a plain brawl or anything in the arena never ends the run.
- The checkpoint is written by every machine whenever anyone saves, into party slots 90-99 in
  rotation. It is only written when all players can save. The host retries every 15 s.
- When the host loads a save from this session or a party slot, everyone goes back with it.
- Go back by choice with `coop_checkpoint` or the pause-menu button, where the game mode allows it.
- Immersive: the checkpoint is a shared autosave, and a lost run loads the newest autosave.
- Hardcore: no checkpoint. A downed player bleeds out for 30 seconds unless a teammate stands over
  them. Bleeding out ends the run with the game's death screen and saves turn Iron. A teammate's
  blow never starts a bleed-out.
- A scripted brawl runs on the machine of the player in it. Teammates' blows are relayed in.

## Arena and PvP

- Friendly fire is off by default.
- `coop_arena` marks a circle on the ground where players can hurt each other anyway.
- Players fight as teams (`coop_team`) or alone. Teammates cannot hurt each other.
- The last side standing wins the round. Everyone stands up healed and is safe for 3 seconds.
- Rounds won are shown in `coop_status` and read out when the arena closes.
- Hits land one round trip late. Do not expect a fair-fight guarantee.
- In Morvin's arena, a teammate who strikes joins the fight on the player's side.

## Talking and trading

- Walk up to a teammate and press interact to trade, challenge to a duel or invite to your arena
  team. The other player must accept.
- Trade opens the game's merchant window with the teammate's bag, minus what they wear. Quest items
  can be traded. Press Trade, and the teammate accepts or refuses. Values need not balance, so gifts
  work.

## Limitations

- No lobby, friends list or invites. Share the code yourselves.
- The host is not fully authoritative. Do not play with people you do not trust.
- Some loot stays per machine: bodies of creatures outside the session, and two players taking from
  one chest at the same moment.
- Under shared loot, a body's loot is rolled once for the party.
- A crate's spilled pickups land in different spots on each machine.
- Random rolls (extra projectiles, thunder strikes, crits) stay per machine. Only the result crosses.
- A blocked hit shows on the attacker's side a moment late.
- Creature summons, and creatures spawned by mods that bypass the API, exist on one machine only.
- The elevator, one-shot triggers and sprite-swap breakables do not sync.
- Only one player at a time can trigger a forbidden area.
- A client's bed and fast travel cost no time.
- A client in an escort sequence gets the quest state, not the group dialogue.
- Untested in-game so far: enemy health rule, save backups, shared loot, session window, world
  object sync, guard reach, body loot, save marks, block verdicts, world memory, arena, cache sharing.

## Saves and backups

- Back up your saves yourself too. What a session writes into a save cannot be undone.
- Before the game overwrites a slot, the mod copies it to `UserData/CoopBackups/Hero_NN/Savegame_NN/`.
  `recent_*` holds the last 10 saves. `session_*` holds the first save of each of the last 3 sessions.
- Slots a session wrote into are marked, and the mod tells you when you play one alone.
  `coop_saves` lists them.
- Restore with `coop_restore` from the main menu.
- At start the mod checks the `VERSION` file in this repo. A build listed as bad cannot host or join.
  No network means allowed.

## Troubleshooting

- Mod does nothing: you need the coop API build.
- Cannot join: check the code, the version, and the relay settings.
- Quest out of sync: run `coop_resync`, or `coop_resync deep`.
- Something not syncing: `coop_status` shows which hooks fired.
- Check a save: `dotnet run --project tools/DrovaCoopMod.SaveDiff -- <baseline> <candidate>` lists
  what the second save holds that the first does not. Arguments are `data.sav` files or `Savegame_NN`
  folders under `%USERPROFILE%\AppData\LocalLow\Just2D\Drova`. Compare a save made before hosting with
  one made during a session.

## Build

```sh
dotnet build src/DrovaCoopMod
dotnet test tests/DrovaCoopMod.Protocol.Tests
git config core.hooksPath .githooks
```

The mod is copied to the game's `Mods` folder, the protocol DLL to `UserLibs`. Override with
`-p:GameDir="D:\Games\Drova - Forsaken Kin"`. Without the game installed, put the coop API DLL in
`.api/`.

A second player without a second game:

```sh
dotnet run --project tools/DrovaCoopMod.HeadlessPeer -- --relay 127.0.0.1 --port 9060 --code ABCD1234 --mirror --name Mirror
```

`--mirror` copies your movement. `--move` walks a circle.
