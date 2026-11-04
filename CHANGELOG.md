# Changelog

## [0.3.1] - 2026-11-04
### Changed
- **A creature's blow on you counts on your own machine.** It lands when it connects on your screen, so damage, dodging, blocking and stagger happen at once, against where you really stand. Before, the host judged it against where you stood a round trip earlier, and your health dropped only when its answer arrived. Only the NPCs of a teammate's scripted brawl are still judged by that teammate, since there a blow only knocks out.
- An enemy that falls far behind under lag slides back into place instead of jumping.

### Fixed
- Talking to a teammate by hovering works with friendly fire on. The teammate counted as an enemy, and the game does not talk to enemies. Inside an arena circle the talk stays closed.
- The host's summons hurt a client again in arena mode with two factions.
- An NPC a quest kills on a client's machine, such as Larea, dies for the host too. Since 0.3.0 the host had refused every such death of a creature near it.
- A second lost run or a second trip back within a minute (the checkpoint button, an older save loaded by the host) takes the teammates back too. Since 0.3.0 they stayed where they were.
- Bandits from Twitch and RandomEvents ambushes look the same for everybody (needs the Drova Modding API 0.8.1).

## [0.3.0] - 2026-11-03
### Added
- **Play with Steam friends:** host "Steam friends" in the window (or `coop_host_steam`). Friends join from Steam ("Join Game" on you), from your invite (Invite button, `coop_invite`), or from the "Steam friends hosting" list. No code, no port to forward. The game runs through Steam's relay network, usually a shorter path than a VPN. Only your Steam friends can join, unless the game is public. A join from Steam waits until you have loaded a save. Needs the Steam copy of the game.
- **Public games:** tick "Public: anyone can find and join" when hosting over Steam, and players who are not your friends see your game under "Public games" when they join (name, Together or Arena only, players). Only games of the same protocol are listed. With strangers, "Arena only" keeps your world your own (`PublicGame` setting).
- **Open the port automatically:** when hosting by address, "More options" can have the router open the UDP port through UPnP while you host, and close it afterwards (`PortMapping` setting, off by default). The router must allow UPnP.
- On a Steam Deck or in Big Picture, Steam's on-screen keyboard opens when you tap a text field in the session window.
- Players you played with over Steam show up under Recent players in Steam.
- A teammate turned into a creature uses the creature's other attacks on your screen too: after a weapon swap (a bear's dash bite, a harpy's fan and throw) their copy switches with them.
- **Only the host starts quest dialogues** (session rule, `HostLeadsStory`, off by default): the other players do not see the dialogue options that start a quest, so the story only ever runs in the host's world.

### Changed
- "Over the internet" with a session code is gone, together with `coop_host`, `coop_join`, the session password and the `RelayAddress`, `RelayPort`, `SessionCode` and `SessionPassword` settings. Play over the internet through Steam friends, or host "Network or address" on a forwarded port and have friends join by address.
- **Helping a teammate up is an interaction:** a downed teammate shows the game's hand prompt. Hold the interact key to get them up. Just standing next to them no longer does it, unless the host turns on the new session rule "Revive by standing near" (`StandNearRevive`).
- The protocol changed: every player needs this version.
- Needs the Drova Modding API 0.8.0 or later.

### Fixed
- A player turned into a creature hit their teammates with friendly fire off: a harpy's throws drew blood on the teammate on the morphed player's screen. Their blows on teammates now follow the friendly fire rule (and the arena's).
- A mole's dirt throw at the client covered the host's screen in dirt. The client's screen gets the dirt now, the host's stays clean.
- A creature the client fought out of the host's view froze after the first hit (boars, and any creature with a hit freeze): its hit freeze only wore off while the host could see it, so the client could kill it without a fight. It now keeps fighting.
- Creatures fighting the client far from the host's view landed fewer blows the farther the host stood (a quarter as many at 500). The game switches hit boxes off away from the camera. Around a teammate they now stay on.
- A dodge did not protect the client from host-driven creatures: their blows reached the client after the dodge was over, aimed at where the client's body had been. A creature's blow that arrives during a dodge or just after it now misses, as it does for the host.
- The client missed some creature attacks (a frog's tongue after its spit, quick repeats). Its copy was still finishing the previous attack and dropped the next one. It now cuts the late one short.
- Host and client could disagree on whom a creature fights, so attacks aimed at its target (a frog's mortar, leaps, spawns at the target's feet) could land on a different player on the client. The host now sends whom each creature fights.
- Creatures the host runs shook on the client when they fought next to the client's player (the WaterAmbusher worst). The copy was pushed by its own attacks and by the game's overlap handling against the client's player, while the host's creature stood still. A standing creature now holds the host's spot and stays solid.
- Charges and lunges of creatures the host runs trailed behind on the client. The client now leads them by their speed and the connection delay.
- The WaterDevil (the mass under the water in the Morbid Moor) only went for the host. It attacks only players inside its zone, and the zone only ever saw the host's own player. It now goes for the client too.
- A brawl started from a dialogue could only be fought by the player who started it. When the client started it, the host's swings did not count and the NPC showed no health bar there. Now everyone can join in, and everyone sees the NPC's health.
- Teammates who join such a brawl (the bandits after Boris pays you, Fawini's bodyguard, duels included) are part of the fight now: the NPCs attack them too, and going down there is a knockout, never a bleed-out or a lost run. The brawl ends when one side is all down, and a teammate knocked out of it stays out. This works whoever started the brawl, the host or a client, for every player in the session.
- A quest scene started from a dialogue ran again when a teammate picked the same answer later (Proximus' "Wanna Bet?" fight: the fighters ported twice and one stayed stuck in the arena). While a teammate's scene runs, that dialogue option is hidden for everyone else until a minute after it ends.

## [0.2.0] - 2026-10-26
### Added
- **Modding API content in co-op.** Needs the Modding API 0.7.0 or later.
  - Joining compares both players' content files under `Mods/Modding_API` (quests, placements, items, map layouts, dialogues) and reports a difference.
  - A JSON-placed creature exists once on each machine, and its death reaches both.
  - JSON-placed NPCs are driven by the host like the game's own NPCs.
  - Custom quests' journal entries reach the teammate.
  - A trap or other map-layout object used up on one machine is gone on every machine.
  - NPC routines through map-editor gates work in a session.
- **Morph spells:** a morphed teammate shows as the creature on other machines and turns human again when the morph ends. Killing the creature puts the player down. It does not end the run.
- **Doors sync:** a door one player opens, closes or unlocks does the same for everyone, including players who join later.
- **Unlocks sync:** a chest, door or trapdoor opened with a key or a lockpick is unlocked for everyone, including late joiners. Only the lock crosses. A chest's lid stays as each player left it.
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
