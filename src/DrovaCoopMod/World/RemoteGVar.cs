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
    /// Applies the global-variable changes the session reported, the far half of <see cref="GVarWatch"/>.
    /// A batch stores every value first and runs every consequence second, so no listener reads a half-applied story beat.
    /// </summary>
    internal static class RemoteGVar
    {
        // A catch-up is a whole playthrough, and each consequence is a listener cascade: all in one frame is a visible freeze.
        private const int MaxPerFrame = 64;

        private const int MaxRetries = 3;

        /// <summary>How long a catch-up waits for records the host announced but that have not arrived.</summary>
        private const float CatchUpPatienceSeconds = 10f;

        private static readonly AssertionLog Log = new();
        private static readonly List<GVarChangeMessage> CatchUp = new();
        private static readonly List<GVarChangeMessage> Batch = new();
        private static readonly List<(AGVarBase Gvar, object Previous, object Current, string Name)> ReplayQueue = new();
        private static readonly List<(GVarChangeMessage Message, int Tries)> Retry = new();
        private static readonly Complaints ComplaintLog = new();

        private static bool _catchUpComplete;
        private static int _catchUpStored;
        private static uint _catchUpExpected;
        private static bool _catchUpReplayUnchanged;
        private static float _catchUpWaited;

        /// <summary>How long this player's own write outranks a catch-up.</summary>
        private const float OwnWriteSeconds = 15f;

        // Own recent writes by "list/var": a catch-up built before them must not overwrite them, and they are told once it lands.
        private static readonly Dictionary<string, (AGVarBase Var, float At)> OwnWrites = new(StringComparer.Ordinal);

        /// <summary>Notes a write this player made to a shared variable.</summary>
        internal static void NoteOwnWrite(AGVarBase gvar, string listGuid, string varId)
        {
            OwnWrites[listGuid + "/" + varId] = (gvar, UnityEngine.Time.unscaledTime);
        }

        private static bool IsOwnRecent(in GVarChangeMessage message)
        {
            return OwnWrites.TryGetValue(message.ListGuid + "/" + message.VarId, out (AGVarBase Var, float At) own)
                && UnityEngine.Time.unscaledTime - own.At < OwnWriteSeconds;
        }

        /// <summary>How many values arrived and were stored here.</summary>
        internal static int Applied { get; private set; }

        /// <summary>How many of those had their consequences run.</summary>
        internal static int Replayed { get; private set; }

        /// <summary>How many the game refused outright. Should be zero.</summary>
        internal static int Refused { get; private set; }

        /// <summary>How many arrived carrying a previous value this world did not hold.</summary>
        internal static int Diverged { get; private set; }

        /// <summary>How many teammate raises of an NPC intent were dropped as late echoes.</summary>
        internal static int EchoesIgnored { get; private set; }

        /// <summary>How many assertions are held back waiting for an earlier one.</summary>
        internal static int Pending => Log.Pending + Retry.Count;

        /// <summary>How many finished quests a teammate's value would have reopened.</summary>
        internal static int RegressionsRefused { get; private set; }

        /// <summary>How many stamps were given up on.</summary>
        internal static int Skipped => Log.Skipped;

        /// <summary>Kill switch: values still land, but their consequences are not run.</summary>
        internal static bool Quiet { get; set; }

        /// <summary>Forgets everything. Called when a session ends and after a savegame load.</summary>
        internal static void Reset()
        {
            GVarWatch.Reset();

            Log.Reset();
            Batch.Clear();
            ReplayQueue.Clear();
            Retry.Clear();
            ComplaintLog.Clear();

            Applied = 0;
            Replayed = 0;
            Refused = 0;
            Diverged = 0;
            RegressionsRefused = 0;
        }

        // Only when the session ends: the host's terminator often arrives during a loading screen, which resets the world every frame.
        private static void ForgetCatchUp()
        {
            CatchUp.Clear();
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

        /// <summary>The host has sent the whole catch-up.</summary>
        /// <param name="recordCount">How many records it believes it sent.</param>
        /// <param name="replayUnchanged">Also run the consequences of values this world already held, to repair a save that stored them silently.</param>
        internal static void CompleteCatchUp(uint recordCount, bool replayUnchanged)
        {
            _catchUpComplete = true;
            _catchUpExpected = recordCount;
            _catchUpReplayUnchanged = replayUnchanged;
        }

        /// <summary>Applies everything that has arrived. Call once per frame from a loaded world.</summary>
        internal static void Update()
        {
            if (!CoopSession.IsLive)
            {
                Reset();
                ForgetCatchUp();
                return;
            }

            // The game silently discards a variable write while the active scene is not loaded.
            if (!GVarEvents.IsWriteAccepted()) return;

            Log.Tick(UnityEngine.Time.unscaledDeltaTime);

            while (CoopSession.TryTakeGVarChange(out GVarChangeMessage message))
            {
                Log.Offer(message);
            }

            while (CoopSession.TryTakeGVarCatchup(out GVarChangeMessage record))
            {
                CatchUp.Add(record);
            }

            int budget = MaxPerFrame;

            budget -= ApplyRetries(budget);

            // Live values wait while a catch-up lands: it is older and would overwrite them over the following frames.
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

        /// <summary>Forgets the ordering history of a player who left, since the next holder of the id stamps from one again.</summary>
        internal static void ForgetOrigin(byte origin)
        {
            Log.Forget(origin);
        }

        // The terminator arrives on the network thread, possibly before the last record is drained, so the announced count settles it.
        // A shortfall that never resolves is applied anyway, with a warning.
        private static bool IsCatchUpReady()
        {
            if (!_catchUpComplete) return false;
            if (CatchUp.Count >= _catchUpExpected) return true;

            _catchUpWaited += UnityEngine.Time.unscaledDeltaTime;

            if (_catchUpWaited < CatchUpPatienceSeconds) return false;

            MelonLogger.Warning($"[GVar] the catch-up announced {_catchUpExpected} value(s) and " +
                $"{CatchUp.Count} arrived. Applying what is here; this world may be behind the host's.");

            return true;
        }

        /// <returns>How much of the budget was spent.</returns>
        private static int ApplyLive(int budget)
        {
            if (budget <= 0) return 0;

            Batch.Clear();

            while (Batch.Count < budget && Log.TryTake(out GVarChangeMessage message))
            {
                Batch.Add(message);
            }

            if (Batch.Count == 0) return 0;

            return Apply(Batch, replayUnchanged: false);
        }

        // Stored across frames, but nothing is replayed until the last value has landed.
        private static void ApplyCatchUp(int budget)
        {
            if (budget <= 0) return;

            if (_catchUpStored < CatchUp.Count)
            {
                int end = Math.Min(CatchUp.Count, _catchUpStored + budget);

                Batch.Clear();

                for (int index = _catchUpStored; index < end; index++)
                {
                    if (!IsOwnRecent(CatchUp[index])) Batch.Add(CatchUp[index]);
                }

                _catchUpStored = end;

                Store(Batch, _catchUpReplayUnchanged);
                return;
            }

            Replay(budget);

            if (ReplayQueue.Count > 0) return;

            MelonLogger.Msg($"[GVar] catch-up complete: {CatchUp.Count} value(s) received of " +
                $"{_catchUpExpected} announced, {Applied} stored, {Replayed} consequence(s) run, " +
                $"{Refused} refused.");

            CatchUp.Clear();
            _catchUpStored = 0;
            _catchUpComplete = false;
            _catchUpWaited = 0f;

            // A write made while the session was still being joined was never told at all.
            float now = UnityEngine.Time.unscaledTime;
            foreach ((AGVarBase own, float at) in OwnWrites.Values)
            {
                if (now - at < OwnWriteSeconds && own != null) GVarWatch.ReportCurrent(own);
            }

            OwnWrites.Clear();
        }

        /// <returns>How much of the budget was spent.</returns>
        private static int ApplyRetries(int budget)
        {
            if (budget <= 0 || Retry.Count == 0) return 0;

            Batch.Clear();

            int taken = Math.Min(budget, Retry.Count);

            for (int index = 0; index < taken; index++)
            {
                Batch.Add(Retry[index].Message);
            }

            List<(GVarChangeMessage Message, int Tries)> carried = new();

            for (int index = 0; index < taken; index++)
            {
                carried.Add(Retry[index]);
            }

            Retry.RemoveRange(0, taken);

            int spent = Apply(Batch, replayUnchanged: false, carried);
            return spent;
        }

        /// <returns>How much of the budget the batch used.</returns>
        private static int Apply(List<GVarChangeMessage> batch, bool replayUnchanged,
            List<(GVarChangeMessage Message, int Tries)>? attempts = null)
        {
            // Before the values: the quest graph here must know a teammate's node counted already when it gets there.
            foreach (GVarChangeMessage message in batch) GVarDerived.NoteRemoteOperation(message.Operation);

            Store(batch, replayUnchanged, attempts);

            while (ReplayQueue.Count > 0)
            {
                Replay(int.MaxValue);
            }

            return batch.Count;
        }

        // Stores silently and queues what has to be replayed afterwards.
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

                    // Checked here too: the local mark is per-machine configuration, not necessarily the sender's.
                    if (GVarSyncAccess.IsLocal(list.name, gvar.name)) continue;

                    // A tally this world counts for itself, gated on exact equality with what stands here.
                    if (GVarDerived.IsDerived(message.ListGuid ?? string.Empty, message.VarId ?? string.Empty))
                    {
                        GVarDerived.CountRefusal();

                        if (ComplaintLog.MayComplainAbout(list.name + "." + gvar.name))
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

                    // A finished quest is never reopened: its terminal node has already run here and could never finish it again.
                    if (previous is QuestState had && reported is QuestState wants && IsFinished(had)
                        && !IsFinished(wants))
                    {
                        RegressionsRefused++;

                        if (ComplaintLog.MayComplainAbout(list.name + "." + gvar.name))
                        {
                            MelonLogger.Msg($"[GVar] kept '{list.name}.{gvar.name}' at {had}; player " +
                                $"{message.Origin} holds it at {wants}, and a finished quest is not reopened.");
                        }

                        continue;
                    }

                    // A late echo of an NPC intent this world already raised and lowered would re-arm a played talk.
                    // Only *_Intent lists: routine and location flags must take the host's word.
                    if (message.Kind == GVarChangeMessage.KindBool && message.PreviousIntValue == 0 && message.IntValue != 0
                        && list.name.EndsWith("_Intent", StringComparison.Ordinal)
                        && Equals(previous, false) && GVarWatch.LoweredRecently(message))
                    {
                        EchoesIgnored++;

                        if (ComplaintLog.MayComplainAbout(list.name + "." + gvar.name))
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

                        GVarWatch.Heard(message);
                    }

                    if (changed || replayUnchanged)
                    {
                        ReplayQueue.Add((gvar, previous, reported, list.name + "." + gvar.name));
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Error("[GVar] storing a reported change failed: " + e);
                }
            }
        }

        /// <returns>How many ran.</returns>
        private static int Replay(int budget)
        {
            if (ReplayQueue.Count == 0) return 0;

            if (Quiet)
            {
                int skipped = ReplayQueue.Count;
                ReplayQueue.Clear();
                return skipped;
            }

            int taken = Math.Min(budget, ReplayQueue.Count);
            int ran = 0;

            using (RemoteApply<StoryScope>.Enter())
            using (RemoteApply<GVarScope>.Enter())
            {
                for (int index = 0; index < taken; index++)
                {
                    (AGVarBase gvar, object previous, object current, string _) = ReplayQueue[index];

                    if (GVarEvents.TryReplay(gvar, previous, current))
                    {
                        ran++;
                    }
                }
            }

            ReplayQueue.RemoveRange(0, taken);
            Replayed += ran;

            return ran;
        }

        // Logs, once per variable, a sender whose previous value differs from ours: the divergence names itself when first seen.
        private static void NoteDivergence(GVarList list, AGVarBase gvar, in GVarChangeMessage message,
            object previous)
        {
            // A catch-up value (sequence 0) has no history behind it.
            if (message.Sequence == 0) return;
            if (!GVarEvents.TryDescribe(previous, out byte kind, out int intValue, out float floatValue)) return;
            if (kind != message.Kind) return;

            bool same = kind == GVarChangeMessage.KindFloat
                ? Math.Abs(floatValue - message.PreviousFloatValue) < 0.0001f
                : intValue == message.PreviousIntValue;

            if (same) return;

            Diverged++;

            string name = list.name + "." + gvar.name;
            if (!ComplaintLog.MayComplainAbout(name)) return;

            MelonLogger.Warning($"[GVar] '{name}': player {message.Origin} moved " +
                $"{GVarChangeMessage.Describe(message.Kind, message.PreviousIntValue, message.PreviousFloatValue)} -> " +
                $"{GVarChangeMessage.Describe(message.Kind, message.IntValue, message.FloatValue)}, this world held " +
                $"{GVarChangeMessage.Describe(kind, intValue, floatValue)}. The two worlds had already diverged.");
        }

        private static void Refuse(in GVarChangeMessage message, int tries, string why)
        {
            if (tries + 1 < MaxRetries)
            {
                Retry.Add((message, tries + 1));
                return;
            }

            Refused++;

            string name = (message.ListGuid ?? "?") + "/" + (message.VarId ?? "?");
            if (!ComplaintLog.MayComplainAbout(name)) return;

            MelonLogger.Warning($"[GVar] gave up on a value from player {message.Origin} after " +
                $"{MaxRetries} tries: {why} ({name}). That world has moved and this one has not.");
        }
    }
}
