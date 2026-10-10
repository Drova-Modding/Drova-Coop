using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.GameArena;
using DrovaCoopMod.Systems.Players;
using Il2CppDrova;
using Il2CppDrova.Saveables;
using MelonLoader;

namespace DrovaCoopMod.Systems.Session
{
    /// <summary>
    /// Host-judged end of the run when every player is lethally down or one is dead. Brawl knockouts and the arena never count.
    /// </summary>
    internal sealed class GameOver : ICoopSystem
    {
        // A scripted duel can report its player out with no health for a single frame.
        private const float ConfirmSeconds = 0.75f;

        // Stays set until everyone is up again, since teammates still report "out" until their own load lands.
        private static bool _over;

        private static float _lostFor;

        /// <summary>How many times this machine went back to its latest save for a lost run. For tests.</summary>
        internal static int Count { get; private set; }

        /// <summary>Set while a party load from this class runs, so its arrival is not taken for a hand load.</summary>
        internal static bool PartyLoading { get; set; }

        /// <summary>The host judges the run.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (!CoopSession.HostsWorld || RemotePlayers.Bodies.Count == 0) return;
            if (!PlayerAccess.TryGetPlayer(out Actor player)) return;

            bool localDead = player.IsDead();
            bool anyDead = localDead;
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

            _lostFor += unscaledDeltaTime;
            if (_lostFor < ConfirmSeconds) return;

            _lostFor = 0f;
            _over = true;
            MelonLogger.Msg(anyDead
                ? "[GameOver] a player is dead; the run is lost for everyone."
                : "[GameOver] every player is down; the run is lost for everyone.");

            ushort checkpoint = Checkpoint.Committed;
            ReportGameOver(checkpoint);
            Load(checkpoint);
        }

        /// <summary>A client heard the host end the run, or send everyone back by hand.</summary>
        private static void Take(ushort checkpoint)
        {
            MelonLogger.Msg("[GameOver] the host sent everyone back.");
            Load(checkpoint);
        }

        /// <summary>The host sends everyone back to the party checkpoint on purpose (coop_checkpoint).</summary>
        internal static string BackToCheckpoint()
        {
            if (!CoopSession.HostsWorld) return "Only the host can send everyone back.";
            if (!Checkpoint.AllowsGoingBack) return $"Not in {Checkpoint.Mode} mode: going back by choice is what the mode rules out.";

            ushort checkpoint = Checkpoint.Committed;
            if (checkpoint == 0) return "No party checkpoint yet this session: one is written when someone saves while all of you can.";

            ReportGameOver(checkpoint);
            Load(checkpoint);
            return $"Everyone goes back to checkpoint {checkpoint}.";
        }

        // Loads the named party checkpoint when this machine wrote it, else this hero's latest save.
        private static void Load(ushort checkpoint)
        {
            Count++;
            PartyLoading = true;

            // Stand up before the load, or the loaded body stays in KnockOut mode for good.
            DownedState.Uninstall();

            if (!SavegameGameHandler.TryGet(out SavegameGameHandler handler) || handler == null)
            {
                MelonLogger.Warning("[GameOver] no savegame handler; cannot go back to the latest save.");
                return;
            }

            int hero = handler.GetCurrentHeroIndex();

            // Hardcore ends as a solo death does: the game's own death screen on every machine.
            if (Checkpoint.Mode == GameMode.Hardcore)
            {
                MelonLogger.Msg("[GameOver] Hardcore: the run ends as a death does, for everyone.");

                // The one who died already gets the screen from the death itself.
                bool diedHere = PlayerAccess.TryGetPlayer(out Actor self) && self.IsDead();
                if (!diedHere && GameOverGameHandler.TryGet(out GameOverGameHandler gameOver) && gameOver != null) gameOver.SpawnGameOver();

                // GUI_GameOver.HandlePermaDeath downgrades the hero's saves to Iron itself.
                return;
            }

            if (Checkpoint.TryGetSlot(checkpoint, out int slot))
            {
                MelonLogger.Msg($"[GameOver] back to checkpoint {checkpoint}.");
                if (handler.TryLoadSavegameSync(new SavegameIdentifier(hero, slot), true)) return;
            }

            MelonLogger.Msg("[GameOver] back to this machine's latest save.");
            LoadLatest(handler);
        }

        /// <summary>Loads what the game's own "Load latest" picks for the current hero.</summary>
        internal static void LoadLatest(SavegameGameHandler handler)
        {
            SavegameMetaInfo? latest = SavegameLoader.Get()?.GetLatestHeroMetaInfoSync(handler.GetCurrentHeroIndex());
            if (latest == null || !handler.TryLoadSavegameSync(latest.GetIdentifier(), true))
            {
                MelonLogger.Warning("[Coop] this hero has no save to go back to, or the game refused the load.");
            }
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<GameOverMessage>(MessageIds.GameOver, (_, m) => { if (CoopSession.IsFromHost()) Take(m.Checkpoint); });
        }

        /// <summary>Host to everyone: the run is lost. See <see cref="GameOver"/>.</summary>
        internal static void ReportGameOver(ushort checkpoint)
            => CoopSession.SendIfHost(new GameOverMessage { Checkpoint = checkpoint });
    }
}
