using Drova_Modding_API;
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
    /// Watches every global variable this machine writes, and asserts it to the session.
    ///
    /// <c>AGVar&lt;T&gt;.SetValue</c> is the funnel: every quest advance, dialogue consequence and world
    /// flag goes through it, and each closed generic is its own native method, so all four are patched.
    ///
    /// Two things about that funnel decide the shape of this file, and both were learned the hard way.
    ///
    /// <b>The stamp is taken in the prefix.</b> <c>SetValue</c> runs its whole listener cascade from
    /// inside itself, so a listener that writes a second variable completes - postfix included - before
    /// the outer postfix runs. Reporting the outcome from the postfix therefore puts every effect on
    /// the wire ahead of its own cause, and the receiving world runs the story backwards. The prefix
    /// stamps a monotonic number before anything it causes can happen, and the receiver applies in
    /// stamp order.
    ///
    /// <b>What crosses is an assertion, not a change.</b> The old watch reported only when the value
    /// moved here, which sounds obviously right and is not: <c>SetValue</c> returns early when the
    /// value is already what you are setting, so a machine whose copy of a flag is already <c>false</c>
    /// never tells anybody when the story clears it - and the peer, whose copy is <c>true</c>, keeps it
    /// forever. The mine's day register acquired exactly that stuck bit. So the postfix asserts the
    /// value the variable now holds whenever this machine has not already asserted that value, whether
    /// or not the write moved anything. The dedupe is against what was last said, not against what is
    /// locally true.
    ///
    /// What still does not cross: writes made while a save is streaming in
    /// (<c>AGVarBase.IsGVarLoading</c>), writes this mod itself applied from the wire, and variables a
    /// mod has declared local through <see cref="GVarSyncAccess"/>.
    /// </summary>
    internal static class GVarWatch
    {
        /// <summary>
        /// The GVar lists that describe a per-machine subsystem rather than the world, and therefore
        /// never cross.
        ///
        /// This is the whole remaining declaration. The build before this one also declared, per quest,
        /// which lists were story sequences and which variables were journey-shared or loud - ten list
        /// names, hand-written, of which one named a list that does not exist and six of the bandit
        /// mine's sixteen lists were missing entirely. None of that is needed any more: the apply side
        /// no longer decides anything per variable.
        /// </summary>
        private static readonly string[] MachineLocalLists =
        {
            // The arena's fight-control flags are written every frame by each machine's own arena
            // logic. Synchronized, the two writers overwrite each other and every overwrite crosses
            // again - a per-frame ping-pong that floods the queue and starves real changes.
            "DAS_ArenaSystem",

            // Fast travel is one player's movement, and its list carries a polled port stage direction
            // (PortActorsToPosAwayHelper in the authored assets). Synchronized, one player's trip could
            // move everybody.
            "QuestVars_FastTravel",

            // The weather flags are outputs of each machine's own weather roll, not inputs to it.
            // WeatherSync carries the sky; crossing the flags too is two writers overwriting each
            // other forever.
            "Weather_Config",
        };

        /// <summary>
        /// What this machine last told the session about each variable.
        ///
        /// The only dedupe left, and it is deliberately not "did the value change here". See the class
        /// note: a clearing write that is a local no-op still has to cross.
        /// </summary>
        private static readonly Dictionary<string, long> _lastAsserted = new(StringComparer.Ordinal);

        private static int _reported;
        private static int _refusedLocally;
        private static uint _sequence;
        private static string _trace = string.Empty;

        /// <summary>How many changes were told to the session. For <c>coop_status</c>.</summary>
        internal static int Reported => _reported;

        /// <summary>How many distinct variables this session has spoken about.</summary>
        internal static int Known => _lastAsserted.Count;

        /// <summary>
        /// How many of this world's own writes the game refused, so nothing was told to the session.
        /// Mostly scene teardown, where refusing is correct. For <c>coop_status</c>.
        /// </summary>
        internal static int RefusedLocally => _refusedLocally;

        /// <summary>
        /// A substring; variables whose full name contains it are traced to the log in a release build.
        /// Empty traces nothing. Set by <c>coop_gvar_trace</c>.
        /// </summary>
        internal static string Trace
        {
            get => _trace;
            set => _trace = value ?? string.Empty;
        }

        /// <summary>Forgets the counts and what was said. Called when a session ends.</summary>
        internal static void Reset()
        {
            _reported = 0;
            _refusedLocally = 0;
            _sequence = 0;
            _lastAsserted.Clear();
        }

        /// <summary>
        /// Applies the patches and declares the machine-local lists. Called once at melon init.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            foreach (string listName in MachineLocalLists)
            {
                GVarSyncAccess.MarkListLocal(listName);
            }

            Hooking.TryPrefix(harmony, typeof(AGVar<bool>), nameof(AGVar<bool>.SetValue),
                typeof(GVarWatch), nameof(BoolPrefix));
            Hooking.TryPostfix(harmony, typeof(AGVar<bool>), nameof(AGVar<bool>.SetValue),
                typeof(GVarWatch), nameof(BoolPostfix));

            Hooking.TryPrefix(harmony, typeof(AGVar<int>), nameof(AGVar<int>.SetValue),
                typeof(GVarWatch), nameof(IntPrefix));
            Hooking.TryPostfix(harmony, typeof(AGVar<int>), nameof(AGVar<int>.SetValue),
                typeof(GVarWatch), nameof(IntPostfix));

            Hooking.TryPrefix(harmony, typeof(AGVar<float>), nameof(AGVar<float>.SetValue),
                typeof(GVarWatch), nameof(FloatPrefix));
            Hooking.TryPostfix(harmony, typeof(AGVar<float>), nameof(AGVar<float>.SetValue),
                typeof(GVarWatch), nameof(FloatPostfix));

            Hooking.TryPrefix(harmony, typeof(AGVar<QuestState>), nameof(AGVar<QuestState>.SetValue),
                typeof(GVarWatch), nameof(QuestPrefix));
            Hooking.TryPostfix(harmony, typeof(AGVar<QuestState>), nameof(AGVar<QuestState>.SetValue),
                typeof(GVarWatch), nameof(QuestPostfix));
        }

        /// <summary>
        /// Checks that every declared machine-local list is a list this game actually has.
        ///
        /// Called once the database exists. A declaration that no longer resolves - a renamed asset, a
        /// typo - silently degrades to "this list is ordinary", and the assets prove renames happen:
        /// the shipped lists carry a rename history. The previous build shipped exactly such a typo and
        /// nothing anywhere said so.
        /// </summary>
        internal static void ValidateDeclarations()
        {
            SubDatabase_GVars database = ProviderAccess.GVarDatabase;
            if (database?.AllGVars == null) return;

            List<string> missing = new();

            foreach (string listName in MachineLocalLists)
            {
                bool found = false;

                for (int index = 0; index < database.AllGVars.Count; index++)
                {
                    GVarList list = database.AllGVars[index];

                    if (list != null && list.name == listName)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found) missing.Add(listName);
            }

            if (missing.Count == 0) return;

            MelonLogger.Warning("[GVar] declared machine-local list(s) that this game does not have: " +
                string.Join(", ", missing) + ". Their variables will be synchronized like any other.");
        }

        private static void BoolPrefix(AGVar<bool> __instance, bool t, out Stamp __state)
        {
            __state = Open(GVarChangeMessage.KindBool, __instance.GetValue() ? 1 : 0, 0f, t ? 1 : 0, 0f);
        }

        private static void BoolPostfix(AGVar<bool> __instance, Stamp __state)
        {
            try
            {
                Close(__instance, __state, __instance.GetValue() ? 1 : 0, 0f);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[GVar] watching a bool change failed: " + e);
            }
        }

        private static void IntPrefix(AGVar<int> __instance, int t, out Stamp __state)
        {
            __state = Open(GVarChangeMessage.KindInt, __instance.GetValue(), 0f, t, 0f);
        }

        private static void IntPostfix(AGVar<int> __instance, Stamp __state)
        {
            try
            {
                Close(__instance, __state, __instance.GetValue(), 0f);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[GVar] watching an int change failed: " + e);
            }
        }

        private static void FloatPrefix(AGVar<float> __instance, float t, out Stamp __state)
        {
            __state = Open(GVarChangeMessage.KindFloat, 0, __instance.GetValue(), 0, t);
        }

        private static void FloatPostfix(AGVar<float> __instance, Stamp __state)
        {
            try
            {
                Close(__instance, __state, 0, __instance.GetValue());
            }
            catch (Exception e)
            {
                MelonLogger.Error("[GVar] watching a float change failed: " + e);
            }
        }

        private static void QuestPrefix(AGVar<QuestState> __instance, QuestState t, out Stamp __state)
        {
            __state = Open(GVarChangeMessage.KindQuest, (int)__instance.GetValue(), 0f, (int)t, 0f);
        }

        private static void QuestPostfix(AGVar<QuestState> __instance, Stamp __state)
        {
            try
            {
                Close(__instance, __state, (int)__instance.GetValue(), 0f);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[GVar] watching a quest state change failed: " + e);
            }
        }

        /// <summary>
        /// Takes the stamp, before anything this write causes can happen.
        ///
        /// Cheap on purpose: this runs on every SetValue in the game, changed or not. It reserves a
        /// sequence number only inside a session, because outside one the number would grow forever for
        /// nothing.
        /// </summary>
        private static Stamp Open(byte kind, int previousInt, float previousFloat, int intendedInt,
            float intendedFloat)
        {
            Stamp stamp = default;

            if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined)
            {
                return stamp;
            }

            if (RemoteApply<GVarScope>.InProgress || AGVarBase.IsGVarLoading) return stamp;

            stamp.Kind = kind;
            stamp.PreviousInt = previousInt;
            stamp.PreviousFloat = previousFloat;
            stamp.IntendedInt = intendedInt;
            stamp.IntendedFloat = intendedFloat;
            stamp.Sequence = ++_sequence;

            return stamp;
        }

        /// <summary>
        /// Asserts what the variable now holds, if this machine has not already said so.
        /// </summary>
        private static void Close(AGVarBase gvar, in Stamp stamp, int nowInt, float nowFloat)
        {
            if (stamp.Sequence == 0) return;

            // The session may have ended between prefix and postfix, and the guards are re-checked
            // because a listener of this very write can open a remote-apply scope.
            if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined) return;
            if (RemoteApply<GVarScope>.InProgress || AGVarBase.IsGVarLoading) return;

            GVarList parent = gvar.GetParent();
            if (parent == null) return;

            if (GVarSyncAccess.IsLocal(parent.name, gvar.name)) return;

            string listGuid = parent.Guid;
            string varId = gvar.Id;
            if (string.IsNullOrEmpty(listGuid) || string.IsNullOrEmpty(varId)) return;

            // The game refused the write outright - it does that, silently and with no return value,
            // while the active scene is not loaded. Asserting the old value as though it were news
            // would tell the peer a lie about this world.
            bool landed = stamp.Kind == GVarChangeMessage.KindFloat
                ? Math.Abs(nowFloat - stamp.IntendedFloat) < float.Epsilon
                : nowInt == stamp.IntendedInt;

            if (!landed)
            {
                _refusedLocally++;

                // Only worth a line when the game had no business refusing. During a scene change the
                // active scene is not loaded and SetValue discards every write by design
                // (AGVar.cs:79) - which is most of a quit-to-menu, and warning about each of the
                // forty NPC flags being torn down there buries the one case that matters.
                if (GVarEvents.IsWriteAccepted())
                {
                    MelonLogger.Warning($"[GVar] this world refused its own write to " +
                        $"'{parent.name}.{gvar.name}'; nothing was told to the session.");
                }

                return;
            }

            string key = listGuid + "/" + varId;
            long token = stamp.Kind == GVarChangeMessage.KindFloat
                ? BitConverter.DoubleToInt64Bits(nowFloat)
                : ((long)stamp.Kind << 32) | (uint)nowInt;

            if (_lastAsserted.TryGetValue(key, out long said) && said == token) return;

            _lastAsserted[key] = token;
            _reported++;

            if (_trace.Length > 0)
            {
                string name = parent.name + "." + gvar.name;

                if (name.Contains(_trace, StringComparison.OrdinalIgnoreCase))
                {
                    MelonLogger.Msg($"[GVar] asserted '{name}' = " +
                        $"{Describe(stamp.Kind, nowInt, nowFloat)} (was " +
                        $"{Describe(stamp.Kind, stamp.PreviousInt, stamp.PreviousFloat)}, seq {stamp.Sequence}).");
                }
            }

            CoopSession.ReportGVarChange(new GVarChangeMessage
            {
                ListGuid = listGuid,
                VarId = varId,
                Kind = stamp.Kind,
                IntValue = nowInt,
                FloatValue = nowFloat,
                Origin = CoopSession.LocalPlayerId.Value,
                Sequence = stamp.Sequence,
                PreviousIntValue = stamp.PreviousInt,
                PreviousFloatValue = stamp.PreviousFloat,
            });
        }

        private static string Describe(byte kind, int intValue, float floatValue)
        {
            return kind switch
            {
                GVarChangeMessage.KindBool => intValue != 0 ? "true" : "false",
                GVarChangeMessage.KindFloat => floatValue.ToString("0.###"),
                GVarChangeMessage.KindQuest => ((QuestState)intValue).ToString(),
                _ => intValue.ToString(),
            };
        }

        /// <summary>
        /// What the prefix hands the postfix: the order stamp, what the world held, and what the
        /// caller asked for. A struct with room for every kind rather than a boxed value, because this
        /// is allocated on every variable write the game makes.
        /// </summary>
        internal struct Stamp
        {
            /// <summary>The order stamp, or zero when this write is not being watched.</summary>
            public uint Sequence;

            /// <summary>Which of the four kinds the variable is.</summary>
            public byte Kind;

            /// <summary>What this world held before, for every kind but float.</summary>
            public int PreviousInt;

            /// <summary>What this world held before, when the kind is float.</summary>
            public float PreviousFloat;

            /// <summary>What the caller asked for, for every kind but float.</summary>
            public int IntendedInt;

            /// <summary>What the caller asked for, when the kind is float.</summary>
            public float IntendedFloat;
        }
    }
}
