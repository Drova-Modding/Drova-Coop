using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.Saveables;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Ends the run for everyone when it is lost: every player down from a blow that would have killed them, or one
    /// of them dead - the loser of a deadly duel, which the game kills itself. Everyone then loads their latest save.
    ///
    /// A brawl's knockout never counts (<see cref="Messages.PlayerStateMessage.Out"/>): losing a fistfight is part of
    /// the story, not the end of it. Nor does a player inside the arena circle, where going down is the point.
    ///
    /// The host decides, from the state everybody already sends, so two machines cannot disagree about whether the run
    /// ended. It stays decided until every player is up again: the teammates' last word is "out" until their own load
    /// lands, and judging on that would end the run a second time.
    /// </summary>
    internal static class GameOver
    {
        private static bool _over;

        /// <summary>
        /// How long a lost run has to stay lost before it is called. A scripted duel starting reported its player out with no
        /// health for one frame (the Red Tower's judgment duel), and the run ended before the first blow.
        /// </summary>
        private const float ConfirmSeconds = 0.75f;

        private static float _lostFor;

        /// <summary>How many times this machine went back to its latest save for a lost run. For tests.</summary>
        internal static int Count { get; private set; }

        /// <summary>The host judges the run. Call once per frame.</summary>
        internal static void Update()
        {
            if (CoopSession.State != CoopSessionState.Hosting || RemotePlayers.Bodies.Count == 0) return;
            if (!PlayerAccess.TryGetPlayer(out Actor player)) return;

            bool localDead = player.IsDead();
            bool anyDead = localDead;
            // Going down inside the arena is the point of it, so a player in the circle is never out.
            bool allOut = (DownedState.IsLethallyDown && !Arena.HoldsLocal()) || localDead;
            bool anyOut = DownedState.IsLethallyDown || localDead;

            foreach ((PlayerId id, RemotePlayer other) in RemotePlayers.Bodies)
            {
                anyDead |= other.IsDead;
                allOut &= other.IsDead || (other.IsOut && !Arena.Holds(id));
                anyOut |= other.IsOut;
            }

            if (_over)
            {
                _over = anyOut;
                return;
            }

            if (!anyDead && !allOut)
            {
                _lostFor = 0f;
                return;
            }

            _lostFor += UnityEngine.Time.unscaledDeltaTime;
            if (_lostFor < ConfirmSeconds) return;

            _lostFor = 0f;
            _over = true;
            MelonLogger.Msg(anyDead
                ? "[GameOver] a player is dead; the run is lost for everyone."
                : "[GameOver] every player is down; the run is lost for everyone.");

            ushort checkpoint = Checkpoint.Committed;
            CoopSession.ReportGameOver(checkpoint);
            Load(checkpoint);
        }

        /// <summary>A client heard the host end the run, or send everyone back by hand.</summary>
        internal static void Take(ushort checkpoint)
        {
            MelonLogger.Msg("[GameOver] the host sent everyone back.");
            Load(checkpoint);
        }

        /// <summary>The host sends everyone back to the party checkpoint on purpose (coop_checkpoint).</summary>
        internal static string BackToCheckpoint()
        {
            if (CoopSession.State != CoopSessionState.Hosting) return "Only the host can send everyone back.";
            if (!Checkpoint.AllowsGoingBack) return $"Not in {Checkpoint.Mode} mode: going back by choice is what the mode rules out.";

            ushort checkpoint = Checkpoint.Committed;
            if (checkpoint == 0) return "No party checkpoint yet this session: one is written when someone saves while all of you can.";

            CoopSession.ReportGameOver(checkpoint);
            Load(checkpoint);
            return $"Everyone goes back to checkpoint {checkpoint}.";
        }

        /// <summary>
        /// Back to the party's checkpoint when this machine wrote the one the host names, else to this machine's own
        /// latest save - before the first checkpoint of a session, or if this one's write never happened.
        /// </summary>
        /// <summary>Set while a party load this class started runs, so the load's own arrival is not taken for a hand load.</summary>
        internal static bool PartyLoading { get; set; }

        private static void Load(ushort checkpoint)
        {
            Count++;
            PartyLoading = true;

            // Up before the load, not after: loaded from the floor, the client came back in the KnockOut mode for good -
            // the stand-up then ran against the body from before the load. Uninstall stands them up and drops the
            // guard; the next frame puts it back on whoever the load made the player.
            DownedState.Uninstall();

            if (!SavegameGameHandler.TryGet(out SavegameGameHandler handler) || handler == null)
            {
                MelonLogger.Warning("[GameOver] no savegame handler; cannot go back to the latest save.");
                return;
            }

            int hero = handler.GetCurrentHeroIndex();

            // Hardcore: the lost run ends the way a solo death does - the game's own death screen, on every machine.
            // (Immersive keeps autosaves only, so the latest save below is the newest autosave - the shared one, when the
            // party checkpoint could write it.)
            if (Checkpoint.Mode == GameMode.Hardcore)
            {
                MelonLogger.Msg("[GameOver] Hardcore: the run ends as a death does, for everyone.");

                // The one who died gets the game's own death screen from the death itself; a second would stack on it.
                bool diedHere = PlayerAccess.TryGetPlayer(out Actor self) && self.IsDead();
                if (!diedHere && GameOverGameHandler.TryGet(out GameOverGameHandler gameOver) && gameOver != null) gameOver.SpawnGameOver();

                // Nothing more: that screen is the game's Hardcore death, and the game itself downgrades this hero's
                // saves to Iron there (GUI_GameOver.HandlePermaDeath) and lets the run go on in that mode - on every
                // machine, as it would alone.
                return;
            }

            if (Checkpoint.TryGetSlot(checkpoint, out int slot) && Checkpoint.AllowsGoingBack)
            {
                MelonLogger.Msg($"[GameOver] back to checkpoint {checkpoint}.");
                if (handler.TryLoadSavegameSync(new SavegameIdentifier(hero, slot), true)) return;
            }

            // The newest save of this hero across every slot - what the game's own "load latest" picks.
            MelonLogger.Msg("[GameOver] back to this machine's latest save.");
            SavegameMetaInfo? latest = SavegameLoader.Get()?.GetLatestHeroMetaInfoSync(hero);
            if (latest == null || !handler.TryLoadSavegameSync(latest.GetIdentifier(), true))
            {
                MelonLogger.Warning("[GameOver] this hero has no save to go back to, or the game refused the load.");
            }
        }
    }
}
