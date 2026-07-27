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

## What does not

- **Combat is not synchronized.** A remote body cannot be hit, does no damage, and neither health nor
  death is shared. Fight as though you are alone.
- **Remote bodies do not simulate.** They are carried along their owner's path rather than walking
  themselves, so they do not collide with anything and nothing collides with them.
- **Inventories are not shared.** Equipment is replicated, what is in the bag is not: no pickups, no
  loot rules, no trading. Two players looting the same corpse both get everything.
- **Nothing else is shared**: no quest state, no world flags, no doors, no day and night.
- **Host authority is not enforced.** The host overrides who a state message claims to be from, and
  that is the only check there is.

Treat it as a skeleton with one working limb, and back up your saves.

**Not all of the first list has been seen working yet.** Sessions, identity, the roster, movement, the
pause policy, the ping, remote attacks, bare-handed combos, equipment transfer and per-player chunk
streaming with the two players 2400 units apart have all been watched working against a running game
through a relay. Stance replication, build and voice, and the fix that stops a body snapping when it has
merely fallen behind are written from what those sessions showed but have not themselves been tested.
Treat that second group as the next thing to test, not the next thing to trust.

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

That is what CI runs, and it is all CI can run — everything else references assemblies generated from
the game, which cannot be published. Enable the hook that covers the other half once per clone:

```sh
git config core.hooksPath .githooks
```

## Use it

Open the game's console and:

| Command | Does |
|---------|------|
| `coop_host` | Host through the configured relay. |
| `coop_join [code]` | Join a session. Without an argument, uses the configured code. |
| `coop_leave` | Leave. |
| `coop_status` | Session state, your player id, the session code, whether the session is encrypted, and who is in it with their ping. |

F7, F8 and F9 still host, join and leave, but they have no conflict check and have already collided
with other mods. The commands are the supported route.

Settings live in `UserData/MelonPreferences.cfg` under `[DrovaCoop]`:

```ini
[DrovaCoop]
PlayerName = "Nemeton"
RelayAddress = "relay.example.com"
RelayPort = 9060
SessionCode = ""             # empty when hosting: the relay picks a code with real entropy
SessionPassword = "shared"   # also the encryption key; empty means the relay can read everything
```

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

Debug builds also carry two diagnostics: `#` spawns an inert copy of the player next to you, and `F10`
reports which of the mod's Harmony targets have actually been invoked. The second one exists because a
patch on this game can apply successfully and never run.

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
lib/                                 the API assembly, checked in so CI has one
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
