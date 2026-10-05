using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Il2CppDrova.GlobalVarSystem;
using Il2CppDrova.QuestSystem;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Applies the global-variable changes the session reported.
    ///
    /// The far half of <see cref="GVarWatch"/>, and the file the mine playthroughs kept failing in. It
    /// used to decide, per variable, whether a change was allowed to land at all and whether its
    /// listeners were allowed to run - a tier table of story sequences, journey-shared gates and loud
    /// marks, hand-maintained per quest. Both halves of that were wrong. Values were dropped forever
    /// on a machine whose own copy of the quest had not started yet, which is precisely the machine
    /// that needed them; and the values that did land were stored with the game's save-loading flag
    /// raised, which stores them and tells nothing in the game that they moved. Nothing in Drova polls
    /// a variable, so a value stored that way is invisible to every quest gate, every world observer
    /// and every routine - permanently, because <c>SetValue</c>'s equality check then refuses the
    /// player's own later write as a no-op. Two players holding the same quest with stages that could
    /// never advance was not a bug in the quest; it was this.
    ///
    /// What replaces it has no per-quest knowledge in it at all:
    ///
    /// 1. Nothing is dropped for policy reasons. The only refusals left are physical - the variable
    ///    does not resolve, or the game will not accept a write this frame - and both are counted,
    ///    logged by name, and retried.
    /// 2. A batch is applied in two halves: every value stored first, every consequence run second, so
    ///    no listener ever reads a world that is halfway through a story beat.
    /// 3. The consequences run through the game's own change event, with the handful of observers that
    ///    act on this player's body held shut. See <see cref="StoryEffectGate"/>.
    ///
    /// Applied from the frame loop rather than where the message arrives, because the game may only be
    /// touched from there.
    /// </summary>
    internal static class RemoteGVar
    {
        /// <summary>
        /// How many assertions one frame applies. A catch-up delivers a whole playthrough at once and
        /// every consequence run is a listener cascade, so applying them all in one frame is a visible
        /// freeze right as the world appears.
        /// </summary>
        private const int MaxPerFrame = 64;

        /// <summary>How many times a physically refused assertion is retried before it is reported.</summary>
        private const int MaxRetries = 3;

        /// <summary>How long a catch-up waits for records the host announced but that have not arrived.</summary>
        private const float CatchUpPatienceSeconds = 10f;

        private static readonly AssertionLog _log = new();
        private static readonly List<GVarChangeMessage> _catchUp = new();
        private static readonly List<GVarChangeMessage> _batch = new();
        private static readonly List<(AGVarBase Gvar, object Previous, object Current, string Name)> _replay = new();
        private static readonly List<(GVarChangeMessage Message, int Tries)> _retry = new();
        private static readonly Complaints _complaints = new();

        private static bool _catchUpComplete;
        private static int _catchUpStored;
        private static uint _catchUpExpected;
        private static bool _catchUpReplayUnchanged;
        private static float _catchUpWaited;

        /// <summary>How long this player's own write outranks a catch-up: one is built and crosses in well under this.</summary>
        private const float OwnWriteSeconds = 15f;

        // This player's own recent writes, by "list/var": a catch-up built before them must not overwrite them, and their
        // current values are told once it has landed. Kept also while the session is still connecting, when a write is not
        // told at all. Measured on a 100-220 ms link: a flag set the second after a rejoin was overwritten by the
        // catch-up, and never reached the host either.
        private static readonly Dictionary<string, (AGVarBase Var, float At)> _ownWrites = new(StringComparer.Ordinal);

        /// <summary>Notes a write this player made to a shared variable. Called from GVarWatch.</summary>
        internal static void NoteOwnWrite(AGVarBase gvar, string listGuid, string varId)
        {
            _ownWrites[listGuid + "/" + varId] = (gvar, UnityEngine.Time.unscaledTime);
        }

        private static bool IsOwnRecent(in GVarChangeMessage message)
        {
            return _ownWrites.TryGetValue(message.ListGuid + "/" + message.VarId, out (AGVarBase Var, float At) own)
                && UnityEngine.Time.unscaledTime - own.At < OwnWriteSeconds;
        }

        /// <summary>How many values arrived and were stored here. For <c>coop_status</c>.</summary>
        internal static int Applied { get; private set; }

        /// <summary>How many of those had their consequences run. For <c>coop_status</c>.</summary>
        internal static int Replayed { get; private set; }

        /// <summary>How many the game refused outright. Should be zero. For <c>coop_status</c>.</summary>
        internal static int Refused { get; private set; }

        /// <summary>
        /// How many arrived carrying a previous value this world did not hold - a divergence that had
        /// already happened before this message. For <c>coop_status</c>.
        /// </summary>
        internal static int Diverged { get; private set; }

        /// <summary>How many teammate raises of an NPC intent were dropped as late echoes. For tests.</summary>
        internal static int EchoesIgnored { get; private set; }

        /// <summary>How many assertions are held back waiting for an earlier one.</summary>
        internal static int Pending => _log.Pending + _retry.Count;

        /// <summary>How many finished quests a teammate's value would have reopened. For <c>coop_status</c>.</summary>
        internal static int RegressionsRefused { get; private set; }

        /// <summary>How many stamps were given up on. Every one is a hole in a story.</summary>
        internal static int Skipped => _log.Skipped;

        /// <summary>
        /// Whether consequences are being withheld - the kill switch, for salvaging a session in which
        /// running them turns out to do harm. Values still land; the world simply does not react.
        /// </summary>
        internal static bool Quiet { get; set; }

        /// <summary>Forgets everything. Called when a session ends and after a savegame load.</summary>
        internal static void Reset()
        {
            // The two halves forget together: what this machine said is as stale as what it heard.
            GVarWatch.Reset();

            _log.Reset();
            _batch.Clear();
            _replay.Clear();
            _retry.Clear();
            _complaints.Clear();

            Applied = 0;
            Replayed = 0;
            Refused = 0;
            Diverged = 0;
            RegressionsRefused = 0;
        }

        /// <summary>
        /// Forgets a catch-up in flight. Only when the session ends: the world is reset every frame of
        /// a loading screen, and the host's terminator routinely arrives during one - wiping it there
        /// left the records waiting forever for an end that had already come.
        /// </summary>
        private static void ForgetCatchUp()
        {
            _catchUp.Clear();
            _catchUpComplete = false;
            _catchUpStored = 0;
            _catchUpExpected = 0;
            _catchUpReplayUnchanged = false;
            _catchUpWaited = 0f;
        }

        private static bool IsFinished(QuestState state)
        {
            return state == QuestState.IsCompleted || state == QuestState.HasFailed;
        }

        /// <summary>
        /// The host has sent the whole catch-up.
        /// </summary>
        /// <param name="recordCount">How many records it believes it sent.</param>
        /// <param name="replayUnchanged">
        /// Whether to run the consequences of values this world already held. Repairs a save written
        /// by a build that stored values without ever running them, at the cost of possibly re-running
        /// an authored trigger.
        /// </param>
        internal static void CompleteCatchUp(uint recordCount, bool replayUnchanged)
        {
            _catchUpComplete = true;
            _catchUpExpected = recordCount;
            _catchUpReplayUnchanged = replayUnchanged;
        }

        /// <summary>
        /// Applies everything that has arrived. Call once per frame from a loaded world.
        /// </summary>
        internal static void Update()
        {
            if (!CoopSession.IsLive)
            {
                Reset();
                ForgetCatchUp();
                return;
            }

            // The game discards a variable write outright while the active scene is not loaded, and
            // reports nothing when it does. Holding the queue costs a frame; draining it costs the
            // message, and the previous build could not even tell that it had.
            if (!GVarEvents.IsWriteAccepted()) return;

            _log.Tick(UnityEngine.Time.unscaledDeltaTime);

            while (CoopSession.TryTakeGVarChange(out GVarChangeMessage message))
            {
                _log.Offer(message);
            }

            while (CoopSession.TryTakeGVarCatchup(out GVarChangeMessage record))
            {
                _catchUp.Add(record);
            }

            int budget = MaxPerFrame;

            budget -= ApplyRetries(budget);

            // Live values wait while a catch-up is landing. The catch-up is older than anything live -
            // the host built it before these were written - and it lands over many frames, so a live
            // value applied first is then overwritten by its own history. The log holds them meanwhile.
            if (_catchUpComplete)
            {
                if (IsCatchUpReady())
                {
                    ApplyCatchUp(budget);
                }

                return;
            }

            ApplyLive(budget);
        }

        /// <summary>
        /// Forgets the ordering history of a player who left.
        ///
        /// Ids are reused as soon as they are free, and whoever holds one next starts stamping from one
        /// again. Remembering the previous holder's high-water marks would refuse every one of the
        /// newcomer's writes as a straggler until they caught up with a stranger.
        /// </summary>
        internal static void ForgetOrigin(byte origin)
        {
            _log.Forget(origin);
        }

        /// <summary>
        /// Whether the whole catch-up has arrived and may start landing.
        ///
        /// The terminator is handled on the network thread and the records are drained on the frame
        /// loop, so "the host said it is done" can be true a frame before the last record has been
        /// taken off the queue. The announced count is what settles it. A shortfall that never
        /// resolves is applied anyway, once, and said out loud - stalling forever on a lost record
        /// would be a quieter failure than the one this whole file exists to end.
        /// </summary>
        private static bool IsCatchUpReady()
        {
            if (!_catchUpComplete) return false;
            if (_catchUp.Count >= _catchUpExpected) return true;

            _catchUpWaited += UnityEngine.Time.unscaledDeltaTime;

            if (_catchUpWaited < CatchUpPatienceSeconds) return false;

            MelonLogger.Warning($"[GVar] the catch-up announced {_catchUpExpected} value(s) and " +
                $"{_catchUp.Count} arrived. Applying what is here; this world may be behind the host's.");

            return true;
        }

        /// <summary>
        /// Applies what the ordering log has released, as one batch for this frame.
        /// </summary>
        /// <returns>How much of the budget was spent.</returns>
        private static int ApplyLive(int budget)
        {
            if (budget <= 0) return 0;

            _batch.Clear();

            while (_batch.Count < budget && _log.TryTake(out GVarChangeMessage message))
            {
                _batch.Add(message);
            }

            if (_batch.Count == 0) return 0;

            return Apply(_batch, replayUnchanged: false);
        }

        /// <summary>
        /// Applies the join catch-up: every value stored before any consequence runs.
        ///
        /// Split across frames on purpose - a full playthrough's variables in one frame is a freeze the
        /// moment the world appears - but never split between the two halves. Nothing is replayed
        /// until the last value has landed, so a quest graph waking up cannot read a world that is
        /// halfway through migrating.
        /// </summary>
        private static void ApplyCatchUp(int budget)
        {
            if (budget <= 0) return;

            if (_catchUpStored < _catchUp.Count)
            {
                int end = Math.Min(_catchUp.Count, _catchUpStored + budget);

                _batch.Clear();

                for (int index = _catchUpStored; index < end; index++)
                {
                    if (!IsOwnRecent(_catchUp[index])) _batch.Add(_catchUp[index]);
                }

                _catchUpStored = end;

                Store(_batch, _catchUpReplayUnchanged);
                return;
            }

            // Everything has landed. Now the world may react to it.
            Replay(budget);

            if (_replay.Count > 0) return;

            MelonLogger.Msg($"[GVar] catch-up complete: {_catchUp.Count} value(s) received of " +
                $"{_catchUpExpected} announced, {Applied} stored, {Replayed} consequence(s) run, " +
                $"{Refused} refused.");

            _catchUp.Clear();
            _catchUpStored = 0;
            _catchUpComplete = false;
            _catchUpWaited = 0f;

            // And this player's recent writes told as they stand now: one made while the session was still being joined
            // was never told at all.
            float now = UnityEngine.Time.unscaledTime;
            foreach ((AGVarBase own, float at) in _ownWrites.Values)
            {
                if (now - at < OwnWriteSeconds && own != null) GVarWatch.ReportCurrent(own);
            }

            _ownWrites.Clear();
        }

        /// <summary>
        /// Retries assertions the game physically refused earlier.
        /// </summary>
        /// <returns>How much of the budget was spent.</returns>
        private static int ApplyRetries(int budget)
        {
            if (budget <= 0 || _retry.Count == 0) return 0;

            _batch.Clear();

            int taken = Math.Min(budget, _retry.Count);

            for (int index = 0; index < taken; index++)
            {
                _batch.Add(_retry[index].Message);
            }

            List<(GVarChangeMessage Message, int Tries)> carried = new();

            for (int index = 0; index < taken; index++)
            {
                carried.Add(_retry[index]);
            }

            _retry.RemoveRange(0, taken);

            int spent = Apply(_batch, replayUnchanged: false, carried);
            return spent;
        }

        /// <summary>
        /// Applies one batch: store every value, then run every consequence.
        /// </summary>
        /// <returns>How much of the budget the batch used.</returns>
        private static int Apply(List<GVarChangeMessage> batch, bool replayUnchanged,
            List<(GVarChangeMessage Message, int Tries)>? attempts = null)
        {
            // Before the values: the quest graph here must know a teammate's node counted already when it gets there.
            foreach (GVarChangeMessage message in batch) GVarDerived.NoteRemoteOperation(message.Operation);

            Store(batch, replayUnchanged, attempts);

            while (_replay.Count > 0)
            {
                Replay(int.MaxValue);
            }

            return batch.Count;
        }

        /// <summary>
        /// Stores every value of a batch, silently, and remembers what has to be replayed afterwards.
        /// </summary>
        private static void Store(List<GVarChangeMessage> batch, bool replayUnchanged,
            List<(GVarChangeMessage Message, int Tries)>? attempts = null)
        {
            SubDatabase_GVars database = ProviderAccess.GVarDatabase;
            if (database == null) return;

            for (int index = 0; index < batch.Count; index++)
            {
                GVarChangeMessage message = batch[index];
                int tries = attempts != null && index < attempts.Count ? attempts[index].Tries : 0;

                try
                {
                    GVarList list = database.GetGVarListByGuid(message.ListGuid ?? string.Empty);
                    if (list == null)
                    {
                        Refuse(message, tries, "no list with that guid");
                        continue;
                    }

                    AGVarBase gvar = list.GetGVarById(message.VarId ?? string.Empty);
                    if (gvar == null)
                    {
                        Refuse(message, tries, "no variable with that id");
                        continue;
                    }

                    // Consulted on this side as well. The sender already refused to send anything
                    // marked local, but the mark is per-machine configuration and the machine that
                    // declared a variable local is not necessarily the one that sent the change.
                    if (GVarSyncAccess.IsLocal(list.name, gvar.name)) continue;

                    // A tally this world keeps for itself. Taking somebody else's total would be
                    // worse than doing nothing: the count would stop matching the things actually
                    // standing in this world, and the gate the quest waits on is an exact equality.
                    if (GVarDerived.IsDerived(message.ListGuid ?? string.Empty, message.VarId ?? string.Empty))
                    {
                        GVarDerived.CountRefusal();

                        if (_complaints.MayComplainAbout(list.name + "." + gvar.name))
                        {
                            MelonLogger.Msg($"[GVar] refused a teammate's total for " +
                                $"'{list.name}.{gvar.name}'; this world counts its own.");
                        }

                        continue;
                    }

                    if (!GVarEvents.TryReadReported(message, out object reported))
                    {
                        Refuse(message, MaxRetries, "unknown value kind " + message.Kind);
                        continue;
                    }

                    if (!GVarEvents.TryRead(gvar, out object previous, out _))
                    {
                        Refuse(message, MaxRetries, "not a synchronized type");
                        continue;
                    }

                    NoteDivergence(list, gvar, message, previous);

                    // A finished quest is never reopened by a teammate. The terminal node of its graph
                    // has already run here and is retired, so a quest put back to running can never
                    // finish again - the same unrecoverable shape the old quest reset produced. It
                    // happens when the other world is simply behind this one: an older save, a catch-up
                    // from a host who has not got that far.
                    if (previous is QuestState had && reported is QuestState wants && IsFinished(had)
                        && !IsFinished(wants))
                    {
                        RegressionsRefused++;

                        if (_complaints.MayComplainAbout(list.name + "." + gvar.name))
                        {
                            MelonLogger.Msg($"[GVar] kept '{list.name}.{gvar.name}' at {had}; player " +
                                $"{message.Origin} holds it at {wants}, and a finished quest is not reopened.");
                        }

                        continue;
                    }

                    // A teammate raising a flag this world raised and already took back down: the same event, late.
                    // Taken, it re-armed Mombert's "the vessel, now!" talk after it had played, and the replay hung
                    // on its own finished cutscene with the whole party frozen in it, mid-fight.
                    // Only NPC intents: routine and location flags flip in both worlds all the time and the host's
                    // word on them has to land.
                    if (message.Kind == GVarChangeMessage.KindBool && message.PreviousIntValue == 0 && message.IntValue != 0
                        && list.name.EndsWith("_Intent", StringComparison.Ordinal)
                        && Equals(previous, false) && GVarWatch.LoweredRecently(message))
                    {
                        EchoesIgnored++;

                        if (_complaints.MayComplainAbout(list.name + "." + gvar.name))
                        {
                            MelonLogger.Msg($"[GVar] ignored player {message.Origin} raising '{list.name}.{gvar.name}'; " +
                                "this world raised and used it a moment ago.");
                        }

                        continue;
                    }

                    bool changed = !Equals(previous, reported);

                    if (changed)
                    {
                        using (RemoteApply<GVarScope>.Enter())
                        {
                            if (!GVarEvents.TryStoreSilently(gvar, reported))
                            {
                                Refuse(message, tries, "the game refused the write");
                                continue;
                            }
                        }

                        Applied++;

                        if (Equals(reported, true)) SharedChoices.TeammateWrote(list.name, gvar.name);

                        // This world now holds the teammate's value, so whatever it last said about
                        // the variable no longer describes it. See GVarWatch.Heard.
                        GVarWatch.Heard(message);
                    }

                    if (changed || replayUnchanged)
                    {
                        _replay.Add((gvar, previous, reported, list.name + "." + gvar.name));
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Error("[GVar] storing a reported change failed: " + e);
                }
            }
        }

        /// <summary>
        /// Runs the consequences of everything the store half landed.
        /// </summary>
        /// <returns>How many ran.</returns>
        private static int Replay(int budget)
        {
            if (_replay.Count == 0) return 0;

            // Withheld on request. The values are already in the world; only the reaction is skipped,
            // which is the old behaviour and is here so a session in which running them does harm can
            // be salvaged without a rebuild.
            if (Quiet)
            {
                int skipped = _replay.Count;
                _replay.Clear();
                return skipped;
            }

            int taken = Math.Min(budget, _replay.Count);
            int ran = 0;

            using (RemoteApply<StoryScope>.Enter())
            using (RemoteApply<GVarScope>.Enter())
            {
                for (int index = 0; index < taken; index++)
                {
                    (AGVarBase gvar, object previous, object current, string _) = _replay[index];

                    if (GVarEvents.TryReplay(gvar, previous, current))
                    {
                        ran++;
                    }
                }
            }

            _replay.RemoveRange(0, taken);
            Replayed += ran;

            return ran;
        }

        /// <summary>
        /// Says so, once, when the sender's world did not hold what this one holds.
        ///
        /// The previous value rides along with every assertion and decides nothing. It is here so that
        /// a divergence names itself in the log at the moment it is first observed, instead of being
        /// reconstructed weeks later by diffing two savegames - which is how the day-register defect
        /// was eventually found.
        /// </summary>
        private static void NoteDivergence(GVarList list, AGVarBase gvar, in GVarChangeMessage message,
            object previous)
        {
            // Only meaningful for a live assertion from a peer that stamps them. A catch-up asserts a
            // value with no history behind it.
            if (message.Sequence == 0) return;
            if (!GVarEvents.TryDescribe(previous, out byte kind, out int intValue, out float floatValue)) return;
            if (kind != message.Kind) return;

            bool same = kind == GVarChangeMessage.KindFloat
                ? Math.Abs(floatValue - message.PreviousFloatValue) < 0.0001f
                : intValue == message.PreviousIntValue;

            if (same) return;

            Diverged++;

            string name = list.name + "." + gvar.name;
            if (!_complaints.MayComplainAbout(name)) return;

            MelonLogger.Warning($"[GVar] '{name}': player {message.Origin} moved " +
                $"{GVarChangeMessage.Describe(message.Kind, message.PreviousIntValue, message.PreviousFloatValue)} -> " +
                $"{GVarChangeMessage.Describe(message.Kind, message.IntValue, message.FloatValue)}, this world held " +
                $"{GVarChangeMessage.Describe(kind, intValue, floatValue)}. The two worlds had already diverged.");
        }

        private static void Refuse(in GVarChangeMessage message, int tries, string why)
        {
            if (tries + 1 < MaxRetries)
            {
                _retry.Add((message, tries + 1));
                return;
            }

            Refused++;

            string name = (message.ListGuid ?? "?") + "/" + (message.VarId ?? "?");
            if (!_complaints.MayComplainAbout(name)) return;

            MelonLogger.Warning($"[GVar] gave up on a value from player {message.Origin} after " +
                $"{MaxRetries} tries: {why} ({name}). That world has moved and this one has not.");
        }
    }
}
