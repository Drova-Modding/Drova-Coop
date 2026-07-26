# Drova Coop (skeleton)

A cooperative-play mod for **Drova – Forsaken Kin**, built on the
[Drova Modding API](https://github.com/Drova-Modding/Drova-Modding-API) networking transport.

This is a **skeleton**, and it says so honestly: sessions work end to end — host, join, handshake,
roster, leave, over a relay or directly — but **nothing about the world is synchronised yet**. Two
players can be in one session and will not see each other move. That is the next piece of work, and
it is deliberately not faked here.

## What the layers own

| Layer | Owns |
|-------|------|
| Drova Modding API | Sockets, relay protocol, encryption, message dispatch, connection events. |
| **This mod** | Session state, roster, version handshake, message ids, and later: UI, lobby, world sync. |

Nothing in this repository references LiteNetLib or knows whether a session runs direct or through a
relay. Those are the API's problem, which is what makes the split worth having.

## Requirements

A **coop-enabled** API build — the default build ships no transport at all:

```sh
dotnet build "Drova Modding API.sln" -c ReleaseCoop
```

That produces `Mods/Drova_Modding_API.dll` plus `UserLibs/LiteNetLib.dll`. Both are needed.
`NetworkAccess.IsSupported` is checked at startup, so a plain API build leaves this mod idle with a
clear message instead of crashing.

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

That is what CI runs, and it is all CI can run. Enable the hook that covers the other half once per
clone:

```sh
git config core.hooksPath .githooks
```

## Use it

Settings live in `UserData/MelonPreferences.cfg` under `[DrovaCoop]`:

```ini
[DrovaCoop]
PlayerName = "Nemeton"
RelayAddress = "relay.example.com"
RelayPort = 9060
SessionCode = ""             # empty when hosting: the relay picks a code with real entropy
SessionPassword = "shared"   # also the encryption key; empty means the relay can read everything
```

In a loaded game:

| Key | Does |
|-----|------|
| F7  | Host through the relay. The assigned code is logged and available as `CoopSession.SessionCode`. |
| F8  | Join the code in `SessionCode`. |
| F9  | Leave. |

Hotkeys are scaffolding for testing. A real UI should call `CoopSession` directly and read the same
preferences.

## Layout

```
src/DrovaCoopMod.Protocol/      the wire contract; references the API and nothing else
  Messages/MessageIds.cs        one id per message type, never reused
  Messages/HandshakeMessage.cs, HandshakeResultMessage.cs
  Messages/PlayerStateMessage.cs   the shape world sync should copy

src/DrovaCoopMod/               the melon; references the game
  Core.cs                       melon lifecycle, hotkeys
  CoopPreferences.cs            player-facing settings
  Session/CoopSession.cs        session state, roster, handshake, transport events
  Session/CoopSessionState.cs   Idle / Connecting / Hosting / Joined / Failed

tests/DrovaCoopMod.Protocol.Tests/   serialization round trips, the name cap, id collisions
lib/                            the API assembly, checked in so CI has one
```

The split is not organisational tidiness. Messages must not contain game types — reflecting over
Il2Cpp proxies puts pointers and GC handles on the wire, which mean nothing on the other machine — and
an assembly that holds to that is also the only part of this repository that can be built off a machine
with Drova installed, because the Il2Cpp assemblies are generated from the game and cannot be
published. One rule, two payoffs, enforced by the compiler rather than by remembering.

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

## Next

1. **World sync.** `CoopSession.LatestStates` collects what arrives; nothing applies it yet. Start
   with the remote player as a visible actor, then decide what else is authoritative on the host.
2. **UI.** Host/join/lobby panel, using the API's options-menu and window systems. Show the session
   code and whether the session is encrypted — players deserve to know if the relay can read them.
3. **Save and world state.** Which savegame a session runs against, and what a joining player gets.

## Security notes

- The **host is authoritative** for anything that matters. A client can send any registered message,
  so treat every incoming message as a request rather than a fact.
- With a session password the traffic is encrypted end to end and the relay can neither read nor
  forge it. Without one, a relay operator can do both. Say which of the two is in effect in the UI.
- A relay you do not own is untrusted infrastructure. That is not a reason to avoid one, only a
  reason to set a password.
