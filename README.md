# Drova Coop

A cooperative-play mod for **Drova – Forsaken Kin**, built on the
[Drova Modding API](https://github.com/Drova-Modding/Drova-Modding-API) networking transport.

Two players can share a world and see each other move. That is the honest extent of it — the list
below says what works and what does not, because the gap between those two is most of the project.

## What works

- **Sessions** over a relay or a direct connection: host, join, leave, with a version handshake that
  refuses a mismatched protocol at the door rather than desyncing later.
- **Identity**: the host assigns every player a session-wide id that all machines agree on, and keeps
  a roster that arrives complete for a joining player and incrementally for everyone else.
- **A visible remote player**: a body per player, correctly equipped as the base character, facing the
  right way and playing the right animation, following the path its owner actually took.
- **Time keeps running.** One player opening a menu that would normally freeze the game no longer
  freezes it for everyone else.
- **Console commands** for everything, and an end-to-end ping that measures the link between two
  players rather than to the relay.
- **Attacks are replicated as intent.** What the far player started — an attack, a dodge, a drink — is
  handed to the game's own action module on their body, so the swing and its timing are the game's.
- **Appearance follows the player**: build, voice and the active equipment set. Build is sent separately
  because it is a component on the actor rather than something worn, and the skin item only sets a skin
  id on whatever build is already there — so without it, the right skin lands on the wrong body.
- **Stance is replicated**, which is what puts the weapon in a remote player's hands. Equipping a spear
  is not the same as holding one: outside combat mode it is stowed, and the body performs the spear's
  attack bare-handed.
- **The world streams around every player**, not only around the local one: each remote body carries an
  essential reference point of its own, which is what decides which chunks are loaded.
- **Saves are stripped.** A remote player's body loses every save component before it starts, so it has
  nothing to write with. See below for how to check that a save is unharmed.
- **Session rules in the session window**, set by the host and adopted by everyone: friendly fire, the
  health a downed player stands up with, and enemy health as a percentage of normal. The last one
  scales the party's blows on creatures down rather than raising anything on the creature, so a
  tougher session leaves the savegame exactly as the game would.
- **Rotating save backups.** Nothing this mod relays into the host's save has an inverse, so every
  slot is copied before the game overwrites it, with the state from before each session kept apart.
  See "Use it" for where they are and how to put one back.

- **Creatures are shared.** The host simulates them and every other machine drives copies; a blow on
  one crosses through the game's own damage path, and so do deaths, revives and knockouts. Players can
  hit each other when friendly fire is on. A downed player stands up after a while. A scripted brawl is
  fought on the machine of the player in it, and teammates' blows are relayed in.
- **Quests and world flags follow the party.** Journal entries, map unlocks, kill and dialogue
  experience, knowledge, story-sequence stages, dialogue item grants and the mine's rubble haul cross.
  Loading a save inside a session resyncs; `coop_resync` does it on demand.
- **Day and night** follow the host, including the host's sleep.
- **Loot is a session rule: per player, or shared.** Per player is the game as shipped, every world
  with its own copy of every chest. Shared is one world's worth for the party: a pickup taken is gone
  for everyone, and a chest holds what the last player to open it left. What a chest holds to begin
  with is settled as it comes into view, before anyone opens it, with the host's world winning any
  disagreement. Only chests the game itself filled take part. A chest you store things in, the one your gear is kept in when the story captures
  you, and any chest you put something into stay your own.
- **A withdrawn build refuses to host or join.** The mod reads the `VERSION` file in this repository
  at start, so a build found to corrupt saves can be stopped after it has shipped. Unreachable means
  allowed. See "Use it".

## What does not

- **Some loot stays per machine even when shared.** Corpses and knocked-out bodies, and the pickups a
  breakable cache spills, have no name both worlds agree on, so each world keeps its own. Two players
  taking from one chest in the same moment both get what they took. The bag itself never crosses: no
  trading, no claim on what a teammate is carrying.
- **Random rolls stay per machine**: a talent's second projectile, a thunder strike, a crit on a
  creature. What crosses is the blow that lands, not the roll that made it.
- **Creatures that were never authored into the scene are only half there.** A creature's own summons,
  and creatures spawned at runtime by mods that do not use the API's spawn call, exist on one machine.
- **World objects do not cross.** The mine's rubble blockers are cleared per machine: the quest counter
  crosses, the stones do not. Guard NPCs react to the player whose machine runs their brain and walk
  past the other one.
- **A client's bed and fast travel cost nothing.** The host's sleep moves the day for everyone. A
  client sleeping in an ordinary bed moves nothing and is healed for free, and a client's fast travel
  takes no time.
- **A passenger in an escort sequence does not see the group dialogue.** The host leads every authored
  sequence; a client along for it gets the quest state and the journal, not the lines.
- **Host authority is not enforced.** The host overrides who a message claims to be from, and that is
  the only check there is.
- **No lobby beyond the session window.** No friends list, no invite, no session browser: a code is
  handed over outside the game, by whatever you talk to each other on.

Back up your saves. The mod does it for you as of 0.1.30, but a copy you made yourself is the one you
know is there.

**Not all of the first list has been seen working yet.** Two-player sessions through a relay are the
test bench, and everything above has been watched working in one, with four exceptions as of 0.1.32:
the enemy health rule, the save backups, shared loot (including the chest offers of 0.1.33) and the
session window have never run in a game. `coop_status` and `coop_backups` say whether the hooks fired;
the window says so by appearing. Treat those four as the next thing to test, not the next thing to trust. The options-window section that
used to hold the rules is gone; the rules moved to the window and to `MelonPreferences.cfg`.

## Requirements

A **coop-enabled** API build — the default build ships no transport at all:

```sh
dotnet build "Drova Modding API.sln" -c ReleaseCoop
```

That produces `Mods/Drova_Modding_API.dll` plus `UserLibs/LiteNetLib.dll`. Both are needed.
`NetworkAccess.IsSupported` is checked at startup, so a plain API build leaves this mod idle with a
clear message instead of crashing.

A relay is needed unless both players can reach each other directly. See
[Drova-Coop-Relay](https://github.com/Drova-Modding/Drova-Coop-Relay).

## Build

```sh
dotnet build src/DrovaCoopMod
```

The melon is copied into the game's `Mods` folder automatically and `DrovaCoopMod.Protocol.dll` into
`UserLibs`. Override the path with `-p:GameDir="D:\Games\Drova - Forsaken Kin"`.

The protocol assembly and its tests need no game at all:

```sh
dotnet test tests/DrovaCoopMod.Protocol.Tests
```

…apart from the API itself, which is the one build input here that is not source. A machine with the
game installed already has it. Without one, take `Mods/Drova_Modding_API.dll` out of
`Modding_API_Coop.zip` on the [API's releases](https://github.com/Drova-Modding/Drova-Modding-API/releases)
and put it in `.api/`, which is what CI does. It is deliberately not checked in: a binary sitting
beside the source goes stale with nothing in any diff to say so.

It has to be the **coop** archive. The networking interfaces are compiled behind `NETCOOP`, so the
default build carries none of them and nothing here compiles against it.

That is what CI runs, and it is all CI can run — everything else references assemblies generated from
the game, which cannot be published. Enable the hook that covers the other half once per clone:

```sh
git config core.hooksPath .githooks
```

## Use it

Press **F7** (configurable, see below) or run `coop_window`. The session window has a field for the
session code, a field for the password, and Host, Join, Paste code and Leave buttons, plus the session
rules: friendly fire, shared loot, health after getting up, enemy health. While a session runs it shows
the code to hand on (with a Copy button), whether the relay can read the session, and who is in it with
their ping. Escape closes it. While it is open in a world, the gameplay keys are off, so typing a code
does not move your character.

The console does the same:

| Command | Does |
|---------|------|
| `coop_window` | Open or close the session window. |
| `coop_host` | Host through the configured relay. |
| `coop_join [code]` | Join a session. Without an argument, uses the configured code. |
| `coop_leave` | Leave. |
| `coop_status` | Session state, your player id, the session code, whether the session is encrypted, and who is in it with their ping. |
| `coop_backups` | Where the save backups are and what each slot has on its two shelves. |
| `coop_restore <hero> <slot> [latest\|session\|name]` | Main menu only. Puts a backup back into a slot, after copying the slot as it is now so the restore can be undone. |
| `coop_guids [range]` | Debug builds only. Which creatures can still be found by the guid they report. See below. |

F8 and F9 are gone; F7 opens the window and can be moved with `WindowKey` in case another mod wants it.

Settings live in `UserData/MelonPreferences.cfg` under `[DrovaCoop]`:

```ini
[DrovaCoop]
PlayerName = "Nemeton"
RelayAddress = "relay.example.com"
RelayPort = 9060
SessionCode = ""             # empty when hosting: the relay picks a code with real entropy
SessionPassword = "shared"   # also the encryption key; empty means the relay can read everything
WindowKey = "<Keyboard>/f7"  # opens the session window; empty turns the key off, coop_window still works
FriendlyFire = false         # the four session rules, as the window last set them; only the host's count
SharedLoot = false
RecoveryHealth = 5
EnemyHealth = 100
SaveBackups = 10             # recent copies kept per save slot under UserData/CoopBackups; 0 turns them off
VersionCheckUrl = "https://raw.githubusercontent.com/Drova-Modding/Drova-Coop/master/VERSION"
```

At start the mod fetches `VERSION` from this repository and judges its own build by it: `min` refuses
every older build, `block` refuses a named one or a range, `latest` only tells you a newer one is out.
A build the file refuses cannot host or join, and says why. Everything else reads as allowed: no
network, no file, a line that does not parse. The check is there to stop a build known to damage saves,
not to make a good one depend on a web server. `coop_status` prints what it found. Empty the setting
to turn it off.

Every time the game overwrites a save slot, the mod first copies `data.sav`, `meta.sav` and
`preview.png` to `UserData/CoopBackups/Hero_NN/Savegame_NN/`. Two shelves per slot: `recent_*` holds
the last ten saves whatever was happening, and `session_*` holds the first save after each session began,
which is the last state from before that session touched the slot. The session shelf keeps three. Backups
live under the game folder rather than beside the saves because the save folder is what Steam Cloud
carries. `coop_backups` lists them; `coop_restore` puts one back from the main menu.

## Testing without a second copy of the game

`tools/DrovaCoopMod.HeadlessPeer` joins a session as a second player using the same transport the game
uses. It needs no game and no second machine.

```sh
dotnet run --project tools/DrovaCoopMod.HeadlessPeer -- \
  --relay 127.0.0.1 --port 9060 --code ABCD1234 --mirror --name Mirror
```

`--mirror` copies whatever the other player is doing, a few units to the side. That is the useful mode:
motion invented by the tool has to guess the game's own movement speed, and a wrong guess looks exactly
like a replication fault. `--move` walks a circle instead, for when there is nobody to mirror.

Debug builds also carry three diagnostics: `#` spawns an inert copy of the player next to you, `F10`
reports which of the mod's Harmony targets have actually been invoked, and `coop_guids [range]` reports
which creatures can still be found by the guid they report.

The first exists because a patch on this game can apply successfully and never run. The third exists
because a creature can be registered under one guid and report another, and nothing else notices:

```
120 actors registered; 1 cannot be found by the guid they report.
  MISMATCH Creature_WaterRat(Clone): filed under 3f2a1c7e, reports 9b40dd12 - every lookup by its
    own guid misses it.
within 60 units:
  Creature_WaterRat(Clone) 12u | guid 9b40dd12 (filed 3f2a1c7e) | LOOKUP FAILS | runtime, no handle
    | 40/40 | claimed as enemy 7 NOT-BROADCAST
```

`LOOKUP FAILS` on a claimed creature is the whole diagnosis: the host owns it, cannot find it again,
and therefore never sends a position for it — which on the other machine is a creature only one player
can see. `MISMATCH` says the same thing from the table's side and finds them anywhere in the world,
not only nearby. Everything else on the line — whether the guid was minted here, whether the body has
finished initializing, what the session made of it — is there to tell that case apart from a creature
that is simply out of range or not claimed at all.

## Checking a save is unharmed

A remote body is a clone of the player prefab, and the player's save key is a hardcoded constant, so any
save component that survives the strip writes into the real player's bucket. That is this mod's most
expensive possible bug and none of it is visible while playing.

`tools/DrovaCoopMod.SaveDiff` compares two saves and lists what the second one holds that the first did
not. It parses nothing: the save's type and field names sit in it as plain UTF-16, and a save with an
extra thing in it has an extra name in it.

```sh
dotnet run --project tools/DrovaCoopMod.SaveDiff -- <baseline-save> <candidate-save>
```

Each argument is a `data.sav` or the `Savegame_NN` folder holding one, under
`%USERPROFILE%\AppData\LocalLow\Just2D\Drova`.

Run it against a control, not against zero. Two ordinary saves minutes apart differ by a kilobyte and
report no new names at all, so that is the bar:

1. Load a save with no session running. Save to a spare slot. **That is the baseline.**
2. Host, get a remote body into the world, and save to another spare slot.
3. Diff the two. New names, or counts that moved, mean something about the session reached the save.
4. Load the second save without the mod and confirm the player is intact.

## Layout

```
src/DrovaCoopMod.Protocol/      the wire contract; references the API and nothing else
  Messages/                     one file per message, plus the ids and the protocol version
  Versioning/                   the VERSION policy file's grammar and verdict

src/DrovaCoopMod/               the melon; references the game
  Core.cs                       melon lifecycle
  CoopCommands.cs               console commands
  CoopPreferences.cs            player-facing settings
  Session/                      session state, identity, roster, handshake, ping
  World/                        remote player bodies, equipment, name tags, the pause policy
  Sync/                         the guard that stops replicated changes echoing back
  Diagnostics/                  Debug-only probes

tests/DrovaCoopMod.Protocol.Tests/   serialization, the name cap, roster bounds, id collisions
tools/DrovaCoopMod.HeadlessPeer/     a second player without a second game
tools/DrovaCoopMod.SaveDiff/         what a session put into a save, if anything
```

The split between the two `src` projects is not organizational tidiness. Messages must not contain
game types — reflecting over Il2Cpp proxies puts pointers and GC handles on the wire, which mean
nothing on the other machine — and an assembly that holds to that is also the only part of this
repository that can be built off a machine with Drova installed. One rule, two payoffs, enforced by
the compiler rather than by remembering.

## Conventions worth keeping

- **Message ids are a contract.** They live in `MessageIds` and never change meaning. Add gaps, don't
  renumber — an id that means something else in the next version is a corrupt packet that passes
  every check.
- **Messages are structs with blittable fields.** Sending and receiving then allocates nothing, even
  at one message per frame per player. A `string` field is fine for something sent once per session
  (the handshake) and wrong for anything sent continuously.
- **Continuous state goes out unreliable.** The next packet supersedes it, so paying for reliable
  delivery of superseded data wastes bandwidth for worse latency.
- **The version handshake is not optional.** Two mod versions that disagree about a message layout
  desync in ways that look like game bugs. Refusing at the door is cheaper than debugging that.
- **A patch that applies is not a patch that runs.** This build inlines small public methods into
  their callers, and a Harmony patch on one of those is accepted and then silently never invoked. Two
  chokepoints have already turned out this way. Verify by counting invocations, not by checking that
  the patch was applied.

## Security notes

- The **host is authoritative** for anything that matters. A client can send any registered message,
  so treat every incoming message as a request rather than a fact.
- With a session password the traffic is encrypted end to end and the relay can neither read nor
  forge it. Without one, a relay operator can do both. `coop_status` says which of the two is in
  effect.
- A relay you do not own is untrusted infrastructure. That is not a reason to avoid one, only a
  reason to set a password.
