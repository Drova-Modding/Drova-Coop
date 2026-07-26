# lib

`Drova_Modding_API.dll`, checked in so that CI can build `DrovaCoopMod.Protocol` and run its tests.

## Why a binary is in the repository

The protocol assembly needs exactly one build input that is not source: the API. Everything else the
mod references — `Il2Cppmscorlib`, `UnityEngine.CoreModule`, `Unity.InputSystem` — is generated from
the game and cannot be published, which is why the game-facing `DrovaCoopMod` project is not built in
CI at all and is checked locally by the pre-push hook instead.

The API is not one of those. It is our own code, it compiles against no Il2Cpp assembly, and the
networking interfaces the protocol assembly uses are pure primitives.

## Refreshing it

A local build prefers the installed game's copy, so this file is only read when there is no game
directory. Refresh it after an API change that the protocol assembly depends on:

```sh
cp "$GAMEDIR/Mods/Drova_Modding_API.dll" lib/
```

Replace this with a download from the API repository's release artifacts once that repository
publishes them. A checked-in binary goes stale silently; a release artifact has a version.
