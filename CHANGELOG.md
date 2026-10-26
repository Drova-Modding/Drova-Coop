# Changelog

## [0.2.0] - 2026-10-26
### Added
- **Modding API content in co-op.** Needs the Modding API 0.7.0 or later.
  - Joining compares both players' content files under `Mods/Modding_API` (quests, placements, items, map layouts, dialogues) and reports a difference.
  - A JSON-placed creature exists once on each machine, and its death reaches both.
  - JSON-placed NPCs are driven by the host like the game's own NPCs.
  - Custom quests' journal entries reach the teammate.
  - A trap or other map-layout object used up on one machine is gone on every machine.
  - NPC routines through map-editor gates work in a session.
- **Morph spells:** a morphed teammate shows as the creature on other machines and turns human again when the morph ends. Killing the creature puts the player down; it does not end the run.
- **Doors sync:** a door one player opens, closes or unlocks does the same for everyone, including players who join later.
- **Unlocks sync:** a chest, door or trapdoor opened with a key or a lockpick is unlocked for everyone, including late joiners. Only the lock crosses; a chest's lid stays as each player left it.
- **Shared wave spawners:** the game's creature waves (bloodquito nests, the flesh moor) are spawned by the host and shared. A client spawns its own only while the host is not in that spawner's area.
- **Stuns sync:** a creature or player whose guard is broken shows the stagger on every machine. A teammate's body only staggers when its owner does.
- A client whose save differs from the host's only in Insane plays the host's mode for the session, after one reload. The save itself is not changed.

### Changed
- The README lists the new requirements, the allowed ranges of the session rules, and that an empty relay address means direct and local play only.
- Fewer per-frame string conversions and allocations for guid lookups, name tags, talents, the game mode check, game variable tracking, arena flags and enemy snapshots.
- The protocol changed: every player needs this version.

### Fixed
- Downed players got up on their own after about 5 seconds on Insane Hardcore. Insane Hardcore now follows the Hardcore rules.
- After a party reload (both players down), the client was missing some creatures until it reloaded again. The host announced the previous world's corpses into the new world.
- At the game's wave spawners, each machine rolled its own waves, so players saw different creatures and kills stayed on one machine.
- A client still in a dialogue could not skip it when the host finished first, while the NPC (Reija) walked off. The host's cutscene moves and snapshots now wait for the client's talk to end.
- A creature stunned by the host stood idle on the client, and the host's own stagger showed on the client late or when there was none.
- Lock picking failed in a session. The first-time explanation of a minigame pauses the game, and a session keeps time running, so the minigame failed behind it and the pick broke. The explanation then came back the next time. Minigames now wait while their explanation is shown. This also fixes the first fishing and mining minigame.
- Combat music played whenever two players stood together with friendly fire on. Teammates no longer put each other in combat.
- After a trade, the host saw the client's character undressed.
- Ada's fog in the arena did not wake for a client fighting her while the host stood outside the ring, so no doppelgangers appeared.
- In a fight between two creatures far from the host, the host's health refresh healed the wounds the client's fight dealt.
- Creatures built from a mod's spawn recipe could disagree on their maximum health between machines.
- A custom quest's journal entry could pay its reward twice.
- When two players took from the same loot stack at once, the smaller take could win.
- Shared rubble hauling could get stuck on a day without a pile, and a peer could claim another player's haul.
- Hovering over a teammate who had just left threw an error every frame.
- Teammates slid across the map after a teleport or scene change instead of appearing at the new spot.

### Testing
- New scenarios: `stun`, `spawners`, `reload_enemies`, `reija_leaves`, `insane_modes`, `creature_fight`, `door`, `lockpick`, `combat_music`, `password`, `saves`, `perfect_block`, `window_input`, `undead_challenge`, `morph`, `modding_content`, `content_mismatch`, `routine_gate`. `smoke` checks the VERSION fetch.
- New test commands: `locks`, `lockstate`, `door`, `doorunlock`, `combatmusic`, `password`, `perfectwindow`, `playedalone`. The Modding API's agent gained `minigames`, `minigame`, `gameplayinput`, `mainmenu` and `equipped <target>`.

## [0.1.0]
- First release.
