using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Il2CppDrova.GlobalVarSystem;
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

        /// <summary>How often the same variable may name itself in the log, in seconds.</summary>
        private const float RefusalQuietSeconds = 5f;

        /// <summary>How long a catch-up waits for records the host announced but that have not arrived.</summary>
        private const float CatchUpPatienceSeconds = 10f;

        private static readonly AssertionLog _log = new();
        private static readonly List<GVarChangeMessage> _catchUp = new();
        private static readonly List<GVarChangeMessage> _batch = new();
        private static readonly List<(AGVarBase Gvar, object Previous, object Current, string Name)> _replay = new();
        private static readonly List<(GVarChangeMessage Message, int Tries)> _retry = new();
        private static readonly Dictionary<string, float> _lastComplaint = new(StringComparer.Ordinal);

        private static bool _catchUpComplete;
        private static int _catchUpStored;
        private static uint _catchUpExpected;
        private static bool _catchUpReplayUnchanged;
        private static float _catchUpWaited;

        private static int _applied;
        private static int _replayed;
        private static int _refused;
        private static int _diverged;
        private static bool _quiet;

        /// <summary>How many values arrived and were stored here. For <c>coop_status</c>.</summary>
        internal static int Applied => _applied;

        /// <summary>How many of those had their consequences run. For <c>coop_status</c>.</summary>
        internal static int Replayed => _replayed;

        /// <summary>How many the game refused outright. Should be zero. For <c>coop_status</c>.</summary>
        internal static int Refused => _refused;

        /// <summary>
        /// How many arrived carrying a previous value this world did not hold - a divergence that had
        /// already happened before this message. For <c>coop_status</c>.
        /// </summary>
        internal static int Diverged => _diverged;

        /// <summary>How many assertions are held back waiting for an earlier one.</summary>
        internal static int Pending => _log.Pending + _retry.Count;

        /// <summary>How many stamps were given up on. Every one is a hole in a story.</summary>
        internal static int Skipped => _log.Skipped;

        /// <summary>
        /// Whether consequences are being withheld - the kill switch, for salvaging a session in which
        /// running them turns out to do harm. Values still land; the world simply does not react.
        /// </summary>
        internal static bool Quiet
        {
            get => _quiet;
            set => _quiet = value;
        }

        /// <summary>Forgets everything. Called when a session ends and after a savegame load.</summary>
        internal static void Reset()
        {
            _log.Reset();
            _catchUp.Clear();
            _batch.Clear();
            _replay.Clear();
            _retry.Clear();
            _lastComplaint.Clear();

            _catchUpComplete = false;
            _catchUpStored = 0;
            _catchUpExpected = 0;
            _catchUpReplayUnchanged = false;
            _catchUpWaited = 0f;

            _applied = 0;
            _replayed = 0;
            _refused = 0;
            _diverged = 0;
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
            if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined)
            {
                Reset();
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
            budget -= ApplyLive(budget);

            if (IsCatchUpReady())
            {
                ApplyCatchUp(budget);
            }
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
                    _batch.Add(_catchUp[index]);
                }

                _catchUpStored = end;

                Store(_batch, _catchUpReplayUnchanged);
                return;
            }

            // Everything has landed. Now the world may react to it.
            Replay(budget);

            if (_replay.Count > 0) return;

            MelonLogger.Msg($"[GVar] catch-up complete: {_catchUp.Count} value(s) received of " +
                $"{_catchUpExpected} announced, {_applied} stored, {_replayed} consequence(s) run, " +
                $"{_refused} refused.");

            _catchUp.Clear();
            _catchUpStored = 0;
            _catchUpComplete = false;
            _catchUpWaited = 0f;
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

                    if (!GVarEvents.TryReadReported(message, out object reported))
                    {
                        Refuse(message, MaxRetries, "unknown value kind " + message.Kind);
                        continue;
                    }

                    if (!GVarEvents.TryRead(gvar, out object previous))
                    {
                        Refuse(message, MaxRetries, "not a synchronized type");
                        continue;
                    }

                    NoteDivergence(list, gvar, message, previous);

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

                        _applied++;
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
            if (_quiet)
            {
                int skipped = _replay.Count;
                _replay.Clear();
                return skipped;
            }

            int taken = Math.Min(budget, _replay.Count);
            int ran = 0;
            int portsBefore = StoryEffectGate.PortsHeld;

            using (StoryEffectGate.Suppress())
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
            _replayed += ran;

            // The story wanted to move a player, and this world refused - rightly, because the port
            // was authored for whoever stood at the trigger. But a capture is meant to take everyone,
            // so refusing is only half an answer: the follow watch supplies the other half and walks
            // this player to the teammate it happened to.
            //
            // This replaces the arming that used to hang off a hand-written list of which variables
            // count as a story gate. That list was wrong twice over - it missed most of the mine's
            // lists, and it fired on the host's echo of the sender's own write, which tore a freshly
            // captured player back out of their own cutscene. A blocked teleport is the thing itself.
            if (StoryEffectGate.PortsHeld > portsBefore)
            {
                MelonLogger.Msg("[GVar] a teammate's story tried to move this player; the port was " +
                    "held and the follow watch armed instead.");

                GateFollow.Arm();
            }

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

            _diverged++;

            string name = list.name + "." + gvar.name;
            if (!MayComplainAbout(name)) return;

            MelonLogger.Warning($"[GVar] '{name}': player {message.Origin} moved " +
                $"{Describe(message.Kind, message.PreviousIntValue, message.PreviousFloatValue)} -> " +
                $"{Describe(message.Kind, message.IntValue, message.FloatValue)}, this world held " +
                $"{Describe(kind, intValue, floatValue)}. The two worlds had already diverged.");
        }

        private static void Refuse(in GVarChangeMessage message, int tries, string why)
        {
            if (tries + 1 < MaxRetries)
            {
                _retry.Add((message, tries + 1));
                return;
            }

            _refused++;

            string name = (message.ListGuid ?? "?") + "/" + (message.VarId ?? "?");
            if (!MayComplainAbout(name)) return;

            MelonLogger.Warning($"[GVar] gave up on a value from player {message.Origin} after " +
                $"{MaxRetries} tries: {why} ({name}). That world has moved and this one has not.");
        }

        /// <summary>
        /// Rate-limits per variable rather than globally, so one noisy variable cannot hide the one
        /// line that matters.
        /// </summary>
        private static bool MayComplainAbout(string name)
        {
            float now = UnityEngine.Time.unscaledTime;

            if (_lastComplaint.TryGetValue(name, out float last) && now - last < RefusalQuietSeconds)
            {
                return false;
            }

            _lastComplaint[name] = now;
            return true;
        }

        private static string Describe(byte kind, int intValue, float floatValue)
        {
            return kind switch
            {
                GVarChangeMessage.KindBool => intValue != 0 ? "true" : "false",
                GVarChangeMessage.KindFloat => floatValue.ToString("0.###"),
                GVarChangeMessage.KindQuest => ((Il2CppDrova.QuestSystem.QuestState)intValue).ToString(),
                _ => intValue.ToString(),
            };
        }
    }
}
