using DrovaCoopMod.Messages;
using Il2CppDrova;
using DrovaCoopMod.Session;
using Il2CppDrova.Saveables;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// A save every machine writes at the same moment, or nobody does - what a lost run (<see cref="GameOver"/>) goes
    /// back to, so the party comes back together instead of each to whatever it saved last on its own.
    ///
    /// Whenever anyone saves, the host asks every machine whether the game would let it save right now. The game
    /// refuses in a fight, a dialogue, a knockout, a scene change; saving anyway would write a state the game never
    /// meant to be loaded (a save taken on the floor loads on the floor). So one "no" and the old checkpoint stands,
    /// and the host asks again every <see cref="RetrySeconds"/> until everyone is free. All "yes": everyone writes
    /// its party slot (<see cref="SlotFor"/>) at once, past the game's own checks - a moment after saying yes, nothing has changed.
    ///
    /// Remembered per session: a checkpoint from another session was written with other people.
    /// </summary>
    internal static class Checkpoint
    {
        /// <summary>
        /// The party slots: each round's checkpoint goes to the next of these, so the last ten stay loadable on every machine.
        /// Clear of the manual slots, quicksave (42) and autosaves (100, 101) - a teammate's own saves are never written.
        /// </summary>
        private const int FirstSlot = 90;
        private const int SlotCount = 10;

        /// <summary>The party slot a round's checkpoint is written to.</summary>
        internal static int SlotFor(ushort round) => FirstSlot + round % SlotCount;

        /// <summary>Whether a slot is one of the party slots.</summary>
        internal static bool IsPartySlot(int slot) => slot >= FirstSlot && slot < FirstSlot + SlotCount;

        // Which round this machine wrote, this session, into each party slot it used.
        private static readonly Dictionary<int, ushort> _bySlot = new();

        // The host's own saves: which round each slot's last save produced - so loading that save takes the party back
        // with it - and the slot of the save whose round is still open.
        private static readonly Dictionary<int, ushort> _byHostSlot = new();
        private static int _savedSlot = -1;

        /// <summary>The party slot this machine holds a round's checkpoint in, when it wrote that round this session.</summary>
        internal static bool TryGetSlot(ushort round, out int slot)
        {
            slot = SlotFor(round);
            return round != 0 && _bySlot.TryGetValue(slot, out ushort held) && held == round;
        }

        /// <summary>
        /// Host: the round a loaded save stands for - a party slot's own, or the round the host's save into that slot
        /// produced. Loading it is going back to that checkpoint, and the party goes with the host.
        /// </summary>
        internal static bool TryGetRound(int slot, out ushort round)
            => IsPartySlot(slot) ? _bySlot.TryGetValue(slot, out round) : _byHostSlot.TryGetValue(slot, out round);

        private const float RetrySeconds = 15f;
        private const float AnswerSeconds = 5f;

        private static ushort _nextId;
        private static ushort _round;
        private static float _roundAge;
        private static readonly HashSet<int> _ready = new();
        private static float _retryIn = -1f;
        private static bool _waiting;
        private const string SaveRunning = "a save is being written";

        // An Immersive checkpoint is an autosave, which comes back through the save hook: not a new round.
        private static float _skipUntil = -1f;

        /// <summary>
        /// The current savegame's game mode. Default and Story keep the party checkpoint in its own slot; Immersive (the
        /// "ironman" mode: autosaves only) makes it a shared autosave instead, so the mode gains no slot; Hardcore has
        /// none, because a death there is final.
        /// </summary>
        internal static GameMode Mode => SavegameGameHandler.TryGet(out SavegameGameHandler handler) && handler?.CurrentSavegame?.Data != null
            ? Il2Cpp.SavegameDataExtensions.GetGameMode(handler.CurrentSavegame.Data)
            : GameMode.Default;

        /// <summary>Whether this mode lets a party go back to a checkpoint by choice (the pause-menu button, coop_checkpoint).</summary>
        internal static bool AllowsGoingBack => Mode is GameMode.Default or GameMode.Story;

        /// <summary>The checkpoint this machine last wrote this session, or 0.</summary>
        internal static ushort Committed { get; private set; }

        /// <summary>
        /// A save is about to be written. Starts a round: here if this is the host, by asking the host otherwise.
        /// Called from the save hook (SaveBackups), which is the one patch on the save interface.
        /// </summary>
        internal static void NoteSave(SavegameIdentifier id)
        {
            // Hardcore keeps no checkpoint at all: no rounds, so no Busy answers and no retries for the whole session.
            if (IsPartySlot(id.SlotIndex) || !CoopSession.IsLive || Mode == GameMode.Hardcore) return;

            // The host's save into this slot replaces whatever round its last one stood for; the round now opened is it.
            if (CoopSession.State == CoopSessionState.Hosting)
            {
                _byHostSlot.Remove(id.SlotIndex);
                _savedSlot = id.SlotIndex;
            }

            // A window, not a one-shot flag: an Immersive checkpoint's autosave can fail before it writes (no slot), and
            // a flag would then swallow the next real save.
            if (UnityEngine.Time.unscaledTime < _skipUntil) return;

            if (CoopSession.State == CoopSessionState.Hosting) _retryIn = 0f;
            else CoopSession.SendCheckpoint(new CheckpointMessage { Stage = CheckpointMessage.StageAsk });
        }

        /// <summary>Runs the host's round. Call once per frame.</summary>
        internal static void Update(float unscaledDeltaTime)
        {
            if (!CoopSession.IsLive)
            {
                Committed = 0;
                _bySlot.Clear();
                _byHostSlot.Clear();
                _savedSlot = -1;
                _round = 0;
                _retryIn = -1f;
                _skipUntil = -1f;
                _waiting = false;
                _ready.Clear();
                return;
            }

            if (CoopSession.State != CoopSessionState.Hosting) return;

            if (_round != 0)
            {
                _roundAge += unscaledDeltaTime;

                if (_ready.Count >= CoopSession.PeerCount)
                {
                    // Asked again, now: the answers took a while, and a dialogue that started here meanwhile made the host
                    // skip its own write after the teammates had written theirs - two machines holding different checkpoints.
                    string? busy = WhyNot();
                    if (busy != null)
                    {
                        Abandon("this machine cannot save now: " + busy);
                        return;
                    }

                    CoopSession.SendCheckpoint(new CheckpointMessage { Id = _round, Stage = CheckpointMessage.StageCommit });
                    if (Write(_round) && _savedSlot >= 0) _byHostSlot[_savedSlot] = _round;
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
                // The save that started this round is usually still being written: that clears in a moment.
                _retryIn = why == SaveRunning ? 1f : RetrySeconds;
                return;
            }

            _waiting = false;

            _round = ++_nextId == 0 ? ++_nextId : _nextId;
            _roundAge = 0f;
            _ready.Clear();
            CoopSession.SendCheckpoint(new CheckpointMessage { Id = _round, Stage = CheckpointMessage.StageAsk });
        }

        /// <summary>A teammate left: a round waiting on answers is dropped, or it would count the leaver's yes.</summary>
        internal static void PeerLeft(int peer)
        {
            if (_round == 0) return;

            _ready.Remove(peer);
            Abandon("a teammate left");
        }

        /// <summary>A step of a round arrived.</summary>
        internal static void Take(int peer, bool fromHost, CheckpointMessage message)
        {
            if (CoopSession.State == CoopSessionState.Hosting)
            {
                switch (message.Stage)
                {
                    case CheckpointMessage.StageAsk:
                        if (_round == 0) _retryIn = 0f;
                        break;
                    case CheckpointMessage.StageReady when message.Id == _round && _round != 0:
                        _ready.Add(peer);
                        break;
                    case CheckpointMessage.StageBusy when message.Id == _round && _round != 0:
                        Abandon("a teammate cannot save right now");
                        break;
                    // After the commit: a teammate became busy in the round trip between its yes and the commit, and kept
                    // its older checkpoint. A new round soon gives the party a common one again.
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
                CoopSession.SendCheckpoint(new CheckpointMessage
                {
                    Id = message.Id,
                    Stage = WhyNot() == null ? CheckpointMessage.StageReady : CheckpointMessage.StageBusy,
                });
            }
            else if (message.Stage == CheckpointMessage.StageCommit && !Write(message.Id) && Mode != GameMode.Hardcore)
            {
                CoopSession.SendCheckpoint(new CheckpointMessage { Id = message.Id, Stage = CheckpointMessage.StageBusy });
            }
        }

        private static void Abandon(string why)
        {
            MelonLogger.Msg($"[Checkpoint] round {_round} dropped ({why}); the last checkpoint stands, asking again in {RetrySeconds:0}s.");
            _round = 0;
            _retryIn = RetrySeconds;
        }

        /// <summary>
        /// Why the game would refuse a save now, or null. The game's own IsSavingRestricted, taken apart so the log can
        /// say which; the item-action check is left to it.
        /// </summary>
        private static string? WhyNot()
        {
            if (!SavegameGameHandler.TryGet(out SavegameGameHandler handler) || handler == null) return "no save handler";
            if (handler.DisableSaveSystem) return "save system off";
            if (handler.IsSaveProcessRunning) return SaveRunning;
            if (SceneGameHandler.IsChangingScene || SceneGameHandler.IsTeleportingPlayer) return "changing scene";
            if (Il2CppDrova.DialogueNew.DialogueSystemGameHandler.TryGet(out var dialogues) && dialogues != null && dialogues.IsPlayerDialogueRunning()) return "in a dialogue";
            if (GUIGameHandler.TryGet(out GUIGameHandler gui) && gui != null && !gui.IsSavingAllowedByModalWindows()) return "a window is open";

            if (EntityGameHandler.TryGet(out EntityGameHandler entities) && entities?.PlayerActor is { } player)
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

            // Asked again: "ready" may be seconds old, and a save written in a fight or on the floor loads that way. This
            // machine then keeps its older checkpoint, and a lost run sends it to its own latest save instead.
            string? why = WhyNot();
            if (why != null || Mode == GameMode.Hardcore)
            {
                MelonLogger.Msg($"[Checkpoint] checkpoint {id} not written here ({why ?? "Hardcore"}).");
                return false;
            }

            if (Mode == GameMode.Immersive)
            {
                _skipUntil = UnityEngine.Time.unscaledTime + 3f;
                Autosave.AutosaveAsync(null, true);
            }
            else
            {
                handler.SaveCurrentAt(new SavegameIdentifier(handler.GetCurrentHeroIndex(), SlotFor(id)), Autosave.GetSaveOptions(), true);
                _bySlot[SlotFor(id)] = id;
            }

            Committed = id;
            MelonLogger.Msg($"[Checkpoint] checkpoint {id} written to slot {SlotFor(id)}.");
            return true;
        }
    }
}
