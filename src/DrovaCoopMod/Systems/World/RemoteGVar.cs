using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Diagnostics;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Sync;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova.GlobalVarSystem;
using Il2CppDrova.QuestSystem;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.World
{
    /// <summary>
    /// Applies the global-variable changes the session reported, the far half of <see cref="GVarWatch"/>.
    /// A batch stores every value first and runs every consequence second, so no listener reads a half-applied story beat.
    /// </summary>
    internal sealed class RemoteGVar : ICoopSystem
    {
        /// <summary>A catch-up is a whole playthrough and each consequence a listener cascade, so all in one frame would freeze visibly.</summary>
        private const int MaxPerFrame = 64;

        private const int MaxRetries = 3;

        /// <summary>How long a catch-up waits for records the host announced but that have not arrived.</summary>
        private const float CatchUpPatienceSeconds = 10f;

        /// <summary>How long this player's own write outranks a catch-up.</summary>
        private const float OwnWriteSeconds = 15f;

        /// <summary>High because a join snapshot delivers a whole playthrough's moved variables at once.</summary>
        private const int MaxPendingGVarChanges = 4096;

        /// <summary>Sized to the variable database (about 6200), since a late-game catch-up arrives all at once.</summary>
        private const int MaxPendingGVarCatchup = 16384;

        /// <summary>A catch-up is the whole variable database, so one player may ask for it this often at most.</summary>
        private const float ResyncCooldownSeconds = 10f;

        private static readonly AssertionLog Log = new();
        private static readonly List<GVarChangeMessage> CatchUp = new();
        private static readonly List<GVarChangeMessage> Batch = new();
        private static readonly List<(AGVarBase Gvar, object Previous, object Current)> ReplayQueue = new();
        private static readonly List<(GVarChangeMessage Message, int Tries)> Retry = new();
        private static readonly Complaints ComplaintLog = new();

        /// <summary>Own recent writes by "list/var". A catch-up built before them must not overwrite them, and they are told once it lands.</summary>
        private static readonly Dictionary<string, (AGVarBase Var, float At)> OwnWrites = new(StringComparer.Ordinal);

        private static readonly Queue<GVarChangeMessage> IncomingGVars = new();
        private static readonly Queue<GVarChangeMessage> IncomingGVarCatchup = new();
        private static readonly List<INetPeer> ResyncAfterLoad = new();
        private static readonly Dictionary<PlayerId, float> LastResync = new();

        private static bool _catchUpComplete;
        private static int _catchUpStored;
        private static uint _catchUpExpected;
        private static bool _catchUpReplayUnchanged;
        private static float _catchUpWaited;

        /// <summary>Whether the catch-up in flight replays consequences even for unchanged values.</summary>
        private static bool _resyncReplaysUnchanged;
        private static bool _hostLoading;

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

        /// <summary>Notes a write this player made to a shared variable.</summary>
        internal static void NoteOwnWrite(AGVarBase gvar, string listGuid, string varId)
        {
            NoteOwnWrite(gvar, listGuid + "/" + varId);
        }

        /// <summary>Notes a write this player made to a shared variable, by its "list/var" key.</summary>
        internal static void NoteOwnWrite(AGVarBase gvar, string key)
        {
            OwnWrites[key] = (gvar, Time.unscaledTime);
        }

        private static bool IsOwnRecent(in GVarChangeMessage message)
        {
            return OwnWrites.TryGetValue(message.ListGuid + "/" + message.VarId, out (AGVarBase Var, float At) own)
                && Time.unscaledTime - own.At < OwnWriteSeconds;
        }

        private static void Reset()
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

        /// <summary>Only when the session ends, since the host's terminator often arrives during a loading screen, which resets the world every frame.</summary>
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
        private static void CompleteCatchUp(uint recordCount, bool replayUnchanged)
        {
            _catchUpComplete = true;
            _catchUpExpected = recordCount;
            _catchUpReplayUnchanged = replayUnchanged;
        }

        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (!CoopSession.SharesWorld)
            {
                Reset();
                ForgetCatchUp();
                return;
            }

            // The game silently discards a variable write while the active scene is not loaded.
            if (!GVarEvents.IsWriteAccepted()) return;

            Log.Tick(Time.unscaledDeltaTime);

            while (TryTakeGVarChange(out GVarChangeMessage message))
            {
                Log.Offer(message);
            }

            while (TryTakeGVarCatchup(out GVarChangeMessage record))
            {
                if (CatchUp.Count < MaxPendingGVarCatchup) CatchUp.Add(record);
            }

            int budget = MaxPerFrame;

            budget -= ApplyRetries(budget);

            // Live values wait while a catch-up lands, since it is older and would overwrite them over the following frames.
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
        private static void ForgetOrigin(byte origin)
        {
            Log.Forget(origin);
        }

        /// <summary>
        /// The terminator arrives on the network thread, possibly before the last record is drained, so the announced count settles it.
        /// A shortfall that never resolves is applied anyway, with a warning.
        /// </summary>
        private static bool IsCatchUpReady()
        {
            if (!_catchUpComplete) return false;
            if (CatchUp.Count >= _catchUpExpected) return true;

            _catchUpWaited += Time.unscaledDeltaTime;

            if (_catchUpWaited < CatchUpPatienceSeconds) return false;

            MelonLogger.Warning($"[GVar] the catch-up announced {_catchUpExpected} value(s) and " +
                $"{CatchUp.Count} arrived. Applying what is here; this world may be behind the host's.");

            return true;
        }

        private static void ApplyLive(int budget)
        {
            if (budget <= 0) return;

            Batch.Clear();

            while (Batch.Count < budget && Log.TryTake(out GVarChangeMessage message))
            {
                Batch.Add(message);
            }

            if (Batch.Count == 0) return;

            Apply(Batch, replayUnchanged: false);
        }

        /// <summary>Stored across frames, but nothing is replayed until the last value has landed.</summary>
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
            float now = Time.unscaledTime;
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
            // Before the values, so the quest graph here knows a teammate's node counted already when it gets there.
            foreach (GVarChangeMessage message in batch) GVarDerived.NoteRemoteOperation(message.Operation);

            Store(batch, replayUnchanged, attempts);

            while (ReplayQueue.Count > 0)
            {
                Replay(int.MaxValue);
            }

            return batch.Count;
        }

        /// <summary>Stores silently and queues what has to be replayed afterwards.</summary>
        private static void Store(List<GVarChangeMessage> batch, bool replayUnchanged,
            List<(GVarChangeMessage Message, int Tries)>? attempts = null)
        {
            SubDatabase_GVars? database = ProviderAccess.GVarDatabase;
            if (database == null) return;

            for (int index = 0; index < batch.Count; index++)
            {
                GVarChangeMessage message = batch[index];
                int tries = attempts != null && index < attempts.Count ? attempts[index].Tries : 0;

                try
                {
                    GVarList? list = database.GetGVarListByGuid(message.ListGuid ?? string.Empty);
                    if (list == null)
                    {
                        Refuse(message, tries, "no list with that guid");
                        continue;
                    }

                    AGVarBase? gvar = list.GetGVarById(message.VarId ?? string.Empty);
                    if (gvar == null)
                    {
                        Refuse(message, tries, "no variable with that id");
                        continue;
                    }

                    // Checked here too, since the local mark is per-machine configuration, not necessarily the sender's.
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

                    if (!GVarEvents.TryReadReported(message, out object? reported))
                    {
                        Refuse(message, MaxRetries, "unknown value kind " + message.Kind);
                        continue;
                    }

                    if (!GVarEvents.TryRead(gvar, out object? previous, out _))
                    {
                        Refuse(message, MaxRetries, "not a synchronized type");
                        continue;
                    }

                    NoteDivergence(list, gvar, message, previous);

                    // A finished quest is never reopened, since its terminal node has already run here and could never finish it again.
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
                    // Only *_Intent lists, since routine and location flags must take the host's word.
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
                        ReplayQueue.Add((gvar, previous, reported));
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Error("[GVar] storing a reported change failed: " + e);
                }
            }
        }

        private static void Replay(int budget)
        {
            if (ReplayQueue.Count == 0) return;

            if (Quiet)
            {
                ReplayQueue.Clear();
                return;
            }

            int taken = Math.Min(budget, ReplayQueue.Count);
            int ran = 0;

            using (RemoteApply<StoryScope>.Enter())
            using (RemoteApply<GVarScope>.Enter())
            {
                for (int index = 0; index < taken; index++)
                {
                    (AGVarBase gvar, object previous, object current) = ReplayQueue[index];

                    if (GVarEvents.TryReplay(gvar, previous, current))
                    {
                        ran++;
                    }
                }
            }

            ReplayQueue.RemoveRange(0, taken);
            Replayed += ran;
        }

        /// <summary>Logs, once per variable, a sender whose previous value differs from ours.</summary>
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

        void ICoopSystem.RegisterMessages()
        {
            // Never relayed back, since a world reacting to its own story twice tells it twice.
            CoopSession.Register<GVarChangeMessage>(MessageIds.GVarChange, OnGVarChange, worldOnly: true);
            CoopSession.Register<GVarSnapshotMessage>(MessageIds.GVarSnapshot, OnGVarSnapshot, worldOnly: true);
            CoopSession.Register<GVarSnapshotEndMessage>(MessageIds.GVarSnapshotEnd, OnGVarSnapshotEnd, worldOnly: true);
            CoopSession.Register<GVarResyncRequestMessage>(MessageIds.GVarResyncRequest, OnGVarResyncRequest, worldOnly: true);
        }

        void ICoopSystem.EndSession()
        {
            // A load that never landed must not hold the next session's catch-ups.
            _hostLoading = false;
            ResyncAfterLoad.Clear();
            LastResync.Clear();
            IncomingGVars.Clear();
            IncomingGVarCatchup.Clear();
        }

        private static bool TryTakeGVarChange(out GVarChangeMessage message)
            => IncomingGVars.TryDequeue(out message);

        private static bool TryTakeGVarCatchup(out GVarChangeMessage message)
            => IncomingGVarCatchup.TryDequeue(out message);

        /// <summary>Reports a global variable this machine changed. The host applies it and passes it on.</summary>
        internal static void ReportGVarChange(in GVarChangeMessage message)
            => CoopSession.SendIfLive(message);

        private static void OnGVarChange(INetPeer peer, GVarChangeMessage message)
        {
            // The host names the sender. A chosen origin with a top stamp would freeze that player's variables for everyone.
            if (CoopSession.State == CoopSessionState.Hosting)
            {
                if (!CoopSession.TryGetSender(peer, out PlayerId sender)) return;

                message.Origin = sender.Value;
            }

            CoopSession.RelayAndQueue(peer, message, IncomingGVars, MaxPendingGVarChanges, "gvar changes");
        }

        private static void OnGVarSnapshotEnd(INetPeer peer, GVarSnapshotEndMessage message)
        {
            if (!CoopSession.IsFromHost()) return;

            CompleteCatchUp(message.RecordCount, _resyncReplaysUnchanged);
            _resyncReplaysUnchanged = false;
        }

        private static void OnGVarResyncRequest(INetPeer peer, GVarResyncRequestMessage message)
        {
            if (!CoopSession.HostsWorld || !CoopSession.TryGetSender(peer, out PlayerId sender)) return;

            float now = Time.unscaledTime;
            if (LastResync.TryGetValue(sender, out float last) && now - last < ResyncCooldownSeconds)
            {
                MelonLogger.Msg($"[GVar] player {sender.Value} asked for the world's variables again within {ResyncCooldownSeconds:0}s; refused.");
                return;
            }

            LastResync[sender] = now;

            // The host is loading too. Answered now, the catch-up would be read from the world being left.
            if (_hostLoading)
            {
                if (!ResyncAfterLoad.Contains(peer)) ResyncAfterLoad.Add(peer);
                MelonLogger.Msg($"[GVar] player {peer.Id} asked for the world's variables while this world is loading; answering once it has.");
                return;
            }

            MelonLogger.Msg($"[GVar] player {peer.Id} asked for the world's variables again (" +
                (message.Reason == GVarResyncRequestMessage.ReasonSavegameLoaded
                    ? "they loaded a savegame"
                    : "asked for by hand") + "); sending the catch-up.");

            GVarSnapshot.SendTo(peer);
            JournalShare.SendTo(peer);

            // A loaded save lost its map unlocks too.
            MapShare.SendTo(peer);
        }

        /// <summary>The host's own savegame load began. See <see cref="OnGVarResyncRequest"/>.</summary>
        internal static void HostLoadStarted()
        {
            if (CoopSession.HostsWorld) _hostLoading = true;
        }

        /// <summary>The host's own load landed, so the catch-ups asked for meanwhile are answered.</summary>
        internal static void HostLoadFinished()
        {
            _hostLoading = false;
            if (!CoopSession.HostsWorld)
            {
                ResyncAfterLoad.Clear();
                return;
            }

            foreach (INetPeer peer in ResyncAfterLoad)
            {
                GVarSnapshot.SendTo(peer);
                JournalShare.SendTo(peer);
                MapShare.SendTo(peer);
            }

            if (ResyncAfterLoad.Count > 0) MelonLogger.Msg($"[GVar] this world's load landed; sent the catch-up to {ResyncAfterLoad.Count} waiting player(s).");
            ResyncAfterLoad.Clear();
        }

        /// <summary>
        /// Asks the host to send this world's variables again, since loading a save mid-session rewinds this
        /// machine's half of the world.
        /// </summary>
        /// <param name="reason">Why, for the host's log line.</param>
        /// <param name="replayUnchanged">Also run the consequences of values already held, to repair saves that stored values without running them.</param>
        internal static void RequestResync(byte reason, bool replayUnchanged)
        {
            if (!CoopSession.JoinedWorld) return;

            _resyncReplaysUnchanged = replayUnchanged;

            NetworkAccess.SendToAll(new GVarResyncRequestMessage
            {
                Reason = reason,
                ReplayUnchanged = replayUnchanged ? (byte)1 : (byte)0,
            });
        }

        private static void OnGVarSnapshot(INetPeer peer, GVarSnapshotMessage message)
        {
            if (!CoopSession.IsFromHost()) return;
            if (message.Records == null) return;

            // Catch-up records go to their own queue, since they are history and never play a transition.
            foreach (GVarSnapshotMessage.Record record in message.Records)
            {
                CoopSession.Enqueue(IncomingGVarCatchup, new GVarChangeMessage
                {
                    ListGuid = message.ListGuid,
                    VarId = record.VarId,
                    Kind = record.Kind,
                    IntValue = record.IntValue,
                    FloatValue = record.FloatValue,
                }, MaxPendingGVarCatchup, "gvar catch-up records");
            }
        }

        void ICoopSystem.PlayerLeft(PlayerId player)
        {
            LastResync.Remove(player);
            ForgetOrigin(player.Value);
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings) => Reset();
    }
}
