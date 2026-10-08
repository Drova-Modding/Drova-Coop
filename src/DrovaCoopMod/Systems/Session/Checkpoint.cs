using DrovaCoopMod.Messages;
using Il2CppDrova;
using Il2CppDrova.DialogueNew;
using Il2CppDrova.Saveables;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.Session
{
    /// <summary>
    /// A save every machine writes at once or nobody does, which a lost run (<see cref="GameOver"/>) goes back to.
    /// The host asks everyone whether the game would allow a save now, and retries until all say yes.
    /// </summary>
    internal sealed class Checkpoint : ICoopSystem
    {
        /// <summary>Party slots rotate through ten, clear of manual slots, quicksave (42) and autosaves (100, 101).</summary>
        private const int FirstSlot = 90;
        private const int SlotCount = 10;
        private const float RetrySeconds = 15f;
        private const float AnswerSeconds = 5f;
        private const string SaveRunning = "a save is being written";

        // Which round this machine wrote this session into each party slot.
        private static readonly Dictionary<int, ushort> BySlot = new();

        // Host: which round each of its own slots' last save produced, so loading it takes the party back too.
        private static readonly Dictionary<int, ushort> ByHostSlot = new();
        private static readonly HashSet<int> Ready = new();

        private static int _savedSlot = -1;
        private static ushort _nextId;
        private static ushort _round;
        private static float _roundAge;
        private static float _retryIn = -1f;
        private static bool _waiting;

        // An Immersive checkpoint is an autosave that comes back through the save hook, not a new round.
        private static float _skipUntil = -1f;

        /// <summary>
        /// The current save's game mode. Immersive makes the checkpoint a shared autosave, Hardcore has none.
        /// </summary>
        internal static GameMode Mode => SavegameGameHandler.TryGet(out SavegameGameHandler handler) && handler != null && handler.CurrentSavegame?.Data != null
            ? Il2Cpp.SavegameDataExtensions.GetGameMode(handler.CurrentSavegame.Data)
            : GameMode.Default;

        /// <summary>Whether this mode lets a party go back to a checkpoint by choice (the pause-menu button, coop_checkpoint).</summary>
        internal static bool AllowsGoingBack => Mode is GameMode.Default or GameMode.Story;

        /// <summary>The checkpoint this machine last wrote this session, or 0.</summary>
        internal static ushort Committed { get; private set; }

        /// <summary>The party slot this machine holds a round's checkpoint in, when it wrote that round this session.</summary>
        internal static bool TryGetSlot(ushort round, out int slot)
        {
            slot = SlotFor(round);
            return round != 0 && BySlot.TryGetValue(slot, out ushort held) && held == round;
        }

        /// <summary>Host: the round a loaded save stands for, its party slot's own or the round the host's save produced.</summary>
        internal static bool TryGetRound(int slot, out ushort round)
            => IsPartySlot(slot) ? BySlot.TryGetValue(slot, out round) : ByHostSlot.TryGetValue(slot, out round);

        private static int SlotFor(ushort round) => FirstSlot + round % SlotCount;

        private static bool IsPartySlot(int slot) => slot >= FirstSlot && slot < FirstSlot + SlotCount;

        /// <summary>A save is about to be written. Starts a round. Called from the save hook (SaveBackups).</summary>
        internal static void NoteSave(SavegameIdentifier id)
        {
            // Hardcore keeps no checkpoint at all.
            if (IsPartySlot(id.SlotIndex) || !CoopSession.IsLive || Mode == GameMode.Hardcore) return;

            if (CoopSession.State == CoopSessionState.Hosting)
            {
                ByHostSlot.Remove(id.SlotIndex);
                _savedSlot = id.SlotIndex;
            }

            // A window, not a flag, since the Immersive autosave can fail before writing and would swallow the next save.
            if (Time.unscaledTime < _skipUntil) return;

            if (CoopSession.State == CoopSessionState.Hosting) _retryIn = 0f;
            else SendCheckpoint(new CheckpointMessage { Stage = CheckpointMessage.StageAsk });
        }

        /// <summary>Runs the host's round.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (!CoopSession.IsLive)
            {
                Committed = 0;
                BySlot.Clear();
                ByHostSlot.Clear();
                _savedSlot = -1;
                _round = 0;
                _retryIn = -1f;
                _skipUntil = -1f;
                _waiting = false;
                Ready.Clear();
                return;
            }

            if (CoopSession.State != CoopSessionState.Hosting) return;

            if (_round != 0)
            {
                _roundAge += unscaledDeltaTime;

                if (Ready.Count >= CoopSession.PeerCount)
                {
                    // Asked again: something may have started here while answers came in, leaving machines on different checkpoints.
                    string? busy = WhyNot();
                    if (busy != null)
                    {
                        Abandon("this machine cannot save now: " + busy);
                        return;
                    }

                    SendCheckpoint(new CheckpointMessage { Id = _round, Stage = CheckpointMessage.StageCommit });
                    if (Write(_round) && _savedSlot >= 0) ByHostSlot[_savedSlot] = _round;
                    _savedSlot = -1;
                    _round = 0;
                }
                else if (_roundAge > AnswerSeconds)
                {
                    Abandon("a teammate never answered");
                }

                return;
            }

            if (_retryIn < 0f) return;

            _retryIn -= unscaledDeltaTime;
            if (_retryIn > 0f) return;

            _retryIn = -1f;
            if (CoopSession.PeerCount == 0) return;

            string? why = WhyNot();
            if (why != null)
            {
                if (!_waiting) MelonLogger.Msg($"[Checkpoint] this machine cannot save right now ({why}); asking again until it can.");
                _waiting = true;
                // Usually the save that started this round, which clears in a moment.
                _retryIn = why == SaveRunning ? 1f : RetrySeconds;
                return;
            }

            _waiting = false;

            _round = ++_nextId == 0 ? ++_nextId : _nextId;
            _roundAge = 0f;
            Ready.Clear();
            SendCheckpoint(new CheckpointMessage { Id = _round, Stage = CheckpointMessage.StageAsk });
        }

        /// <summary>A teammate left: a round waiting on answers is dropped, or it would count the leaver's yes.</summary>
        internal static void PeerLeft(int peer)
        {
            if (_round == 0) return;

            Ready.Remove(peer);
            Abandon("a teammate left");
        }

        /// <summary>A step of a round arrived.</summary>
        private static void Take(int peer, bool fromHost, CheckpointMessage message)
        {
            if (CoopSession.State == CoopSessionState.Hosting)
            {
                switch (message.Stage)
                {
                    case CheckpointMessage.StageAsk:
                        if (_round == 0) _retryIn = 0f;
                        break;
                    case CheckpointMessage.StageReady when message.Id == _round && _round != 0:
                        Ready.Add(peer);
                        break;
                    case CheckpointMessage.StageBusy when message.Id == _round && _round != 0:
                        Abandon("a teammate cannot save right now");
                        break;
                    // A teammate became busy between its yes and the commit. A new round restores a common checkpoint.
                    case CheckpointMessage.StageBusy when _round == 0 && message.Id == Committed:
                        MelonLogger.Msg($"[Checkpoint] a teammate could not write checkpoint {message.Id}; asking again in {RetrySeconds:0}s.");
                        _retryIn = RetrySeconds;
                        break;
                }

                return;
            }

            if (!fromHost) return;

            if (message.Stage == CheckpointMessage.StageAsk)
            {
                string? busy = WhyNot();
                if (busy != null) MelonLogger.Msg($"[Checkpoint] answered round {message.Id} busy: {busy}.");

                SendCheckpoint(message with
                {
                    Stage = busy == null ? CheckpointMessage.StageReady : CheckpointMessage.StageBusy,
                });
            }
            else if (message.Stage == CheckpointMessage.StageCommit && !Write(message.Id) && Mode != GameMode.Hardcore)
            {
                SendCheckpoint(message with { Stage = CheckpointMessage.StageBusy });
            }
        }

        private static void Abandon(string why)
        {
            MelonLogger.Msg($"[Checkpoint] round {_round} dropped ({why}); the last checkpoint stands, asking again in {RetrySeconds:0}s.");
            _round = 0;
            _retryIn = RetrySeconds;
        }

        /// <summary>Why the game would refuse a save now, or null. IsSavingRestricted taken apart so the log can say which.</summary>
        private static string? WhyNot()
        {
            if (!SavegameGameHandler.TryGet(out SavegameGameHandler handler) || handler == null) return "no save handler";
            if (handler.DisableSaveSystem) return "save system off";
            if (handler.IsSaveProcessRunning) return SaveRunning;
            if (SceneGameHandler.IsChangingScene || SceneGameHandler.IsTeleportingPlayer) return "changing scene";
            if (DialogueSystemGameHandler.TryGet(out DialogueSystemGameHandler? dialogues) && dialogues != null && dialogues.IsPlayerDialogueRunning()) return "in a dialogue";
            if (GUIGameHandler.TryGet(out GUIGameHandler gui) && gui != null && !gui.IsSavingAllowedByModalWindows()) return "a window is open";

            Actor? player = EntityGameHandler.TryGet(out EntityGameHandler entities) ? entities.PlayerActor : null;
            if (player != null)
            {
                if (player.AIModule.IsInCombat()) return "in combat";
                if (player.IsDead()) return "dead";
                if (player.CustomStats.IsKnockout) return "knocked out";
                if (player.CustomStats.IsInBrawl) return "in a brawl";
                if (player.CustomStats.IsInCrime) return "in a crime";
            }

            return handler.IsSavingRestricted() ? "the game blocks saving here" : null;
        }

        /// <summary>Writes the checkpoint here, unless this machine cannot save now. Whether it was written.</summary>
        private static bool Write(ushort id)
        {
            if (!SavegameGameHandler.TryGet(out SavegameGameHandler handler) || handler == null) return false;

            // Asked again, since "ready" may be seconds old and a save taken in a fight loads in a fight.
            string? why = WhyNot();
            if (why != null || Mode == GameMode.Hardcore)
            {
                MelonLogger.Msg($"[Checkpoint] checkpoint {id} not written here ({why ?? "Hardcore"}).");
                return false;
            }

            if (Mode == GameMode.Immersive)
            {
                _skipUntil = Time.unscaledTime + 3f;
                Autosave.AutosaveAsync(null, true);
            }
            else
            {
                handler.SaveCurrentAt(new SavegameIdentifier(handler.GetCurrentHeroIndex(), SlotFor(id)), Autosave.GetSaveOptions(), true);
                BySlot[SlotFor(id)] = id;
            }

            Committed = id;
            MelonLogger.Msg($"[Checkpoint] checkpoint {id} written to slot {SlotFor(id)}.");
            return true;
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<CheckpointMessage>(MessageIds.Checkpoint, (peer, m) => Take(peer.Id, CoopSession.IsFromHost(), m));
        }

        /// <summary>A step of a checkpoint round: host to everyone, or a client to the host. See <see cref="Checkpoint"/>.</summary>
        private static void SendCheckpoint(in CheckpointMessage message)
            => CoopSession.SendIfLive(message);
    }
}
