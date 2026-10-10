using Drova_Modding_API;
using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Sync;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.Systems.World.Patches;
using Il2CppDrova.GlobalVarSystem;
using Il2CppDrova.QuestSystem;
using MelonLoader;

namespace DrovaCoopMod.Systems.World
{
    /// <summary>
    /// Asserts every global variable this machine writes to the session, via the four closed <c>AGVar&lt;T&gt;.SetValue</c> methods.
    /// The stamp is taken in the prefix because SetValue runs its listener cascade inside itself, so effects would otherwise precede their cause.
    /// What crosses is the held value, deduped against what was last said, since SetValue skips no-op writes that a peer still needs.
    /// </summary>
    internal sealed class GVarWatch : ICoopSystem
    {
        /// <summary>How long a flag this world switched off stays deaf to a teammate switching it on.</summary>
        private const float EchoSeconds = 10f;

        /// <summary>GVar lists that describe a per-machine subsystem rather than the world, so they never cross.</summary>
        private static readonly string[] MachineLocalLists =
        [
            // Written every frame by each machine's own arena logic. Synchronized, it ping-pongs forever.
            "DAS_ArenaSystem",

            // One player's travel. Its list carries a polled port direction that would move everybody.
            "QuestVars_FastTravel",

            // Outputs of each machine's own weather roll. WeatherSync carries the sky.
            "Weather_Config",
        ];

        /// <summary>
        /// Variables each machine computes from its own inventory, so the two worlds would overwrite each other forever.
        /// </summary>
        private static readonly (string List, string Var)[] MachineLocalVars =
        [
            ("QuestVars_BeautySleep", "LootedEarPlug"),
            ("QuestVars_BeautySleep", "RoisinHasEarplug"),
            ("QuestVars_TheOutcast", "NumPackagesInInventory"),
            ("QuestVars_IronForFarmers", "PickedItems"),
            ("QuestVars_ItPaidOff", "AmountOfSilverInInventoryRequired"),
            ("QuestVars_ItPaidOff", "RuslanCanRobPlayer"),
            ("QuestVars_FoodForFarmers", "NumFoodItemsOwned"),
            ("EVars_Tiaa", "AmountOfMeatInInventory"),
            ("EVars_Player", "HasKiepeWithRubble"),
            ("EVars_Player", "PlayerCarriesPannier"),

            // Counted per machine for the hits its own player takes.
            ("EVars_Player", "SufferedHits"),
        ];

        // What this machine last told the session per variable, deliberately not "did the value change here".
        private static readonly Dictionary<string, long> LastAsserted = new(StringComparer.Ordinal);

        private static readonly Dictionary<string, float> LoweredAt = new(StringComparer.Ordinal);

        private static uint _sequence;

        /// <summary>How many changes were told to the session.</summary>
        internal static int Reported { get; private set; }

        /// <summary>How many distinct variables this session has spoken about.</summary>
        internal static int Known => LastAsserted.Count;

        /// <summary>How many of this world's own writes the game refused, mostly during scene teardown.</summary>
        internal static int RefusedLocally { get; private set; }

        /// <summary>Variables whose full name contains this substring are traced to the log. Empty traces nothing.</summary>
        internal static string Trace { get; set; } = string.Empty;

        /// <summary>
        /// Forgets the counts and what was said. The stamp is not rewound, because peers drop anything at or below the highest stamp they saw.
        /// </summary>
        internal static void Reset()
        {
            Reported = 0;
            RefusedLocally = 0;
            LastAsserted.Clear();
            LoweredAt.Clear();
        }

        /// <summary>
        /// Whether this world switched the flag off itself a moment ago, so a teammate's raise is the same one-shot event arriving late.
        /// </summary>
        internal static bool LoweredRecently(in GVarChangeMessage message)
        {
            return LoweredAt.TryGetValue((message.ListGuid ?? string.Empty) + "/" + (message.VarId ?? string.Empty), out float at)
                && UnityEngine.Time.unscaledTime - at < EchoSeconds;
        }

        /// <summary>
        /// Records a teammate's value as what this machine last said, so setting it back here is told again.
        /// </summary>
        internal static void Heard(in GVarChangeMessage message)
        {
            LastAsserted[(message.ListGuid ?? string.Empty) + "/" + (message.VarId ?? string.Empty)] =
                Token(message.Kind, message.IntValue, message.FloatValue);
        }

        private static long Token(byte kind, int intValue, float floatValue)
        {
            return kind == GVarChangeMessage.KindFloat
                ? BitConverter.DoubleToInt64Bits(floatValue)
                : ((long)kind << 32) | (uint)intValue;
        }

        /// <summary>Applies the patches and declares the machine-local lists.</summary>
        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            foreach (string listName in MachineLocalLists)
            {
                GVarSyncAccess.MarkListLocal(listName);
            }

            foreach ((string list, string var) in MachineLocalVars)
            {
                GVarSyncAccess.MarkVarLocal(list, var);
            }

            Hooking.TryPrefix(harmony, typeof(AGVar<bool>), nameof(AGVar<bool>.SetValue),
                typeof(GVarWatchPatches), nameof(GVarWatchPatches.BoolPrefix));
            Hooking.TryPostfix(harmony, typeof(AGVar<bool>), nameof(AGVar<bool>.SetValue),
                typeof(GVarWatchPatches), nameof(GVarWatchPatches.BoolPostfix));

            Hooking.TryPrefix(harmony, typeof(AGVar<int>), nameof(AGVar<int>.SetValue),
                typeof(GVarWatchPatches), nameof(GVarWatchPatches.IntPrefix));
            Hooking.TryPostfix(harmony, typeof(AGVar<int>), nameof(AGVar<int>.SetValue),
                typeof(GVarWatchPatches), nameof(GVarWatchPatches.IntPostfix));

            Hooking.TryPrefix(harmony, typeof(AGVar<float>), nameof(AGVar<float>.SetValue),
                typeof(GVarWatchPatches), nameof(GVarWatchPatches.FloatPrefix));
            Hooking.TryPostfix(harmony, typeof(AGVar<float>), nameof(AGVar<float>.SetValue),
                typeof(GVarWatchPatches), nameof(GVarWatchPatches.FloatPostfix));

            Hooking.TryPrefix(harmony, typeof(AGVar<QuestState>), nameof(AGVar<QuestState>.SetValue),
                typeof(GVarWatchPatches), nameof(GVarWatchPatches.QuestPrefix));
            Hooking.TryPostfix(harmony, typeof(AGVar<QuestState>), nameof(AGVar<QuestState>.SetValue),
                typeof(GVarWatchPatches), nameof(GVarWatchPatches.QuestPostfix));
        }

        /// <summary>
        /// Warns about declared machine-local lists the game does not have, since those silently sync like any other.
        /// </summary>
        internal static void ValidateDeclarations()
        {
            SubDatabase_GVars? database = ProviderAccess.GVarDatabase;
            if (database == null || database.AllGVars == null) return;

            List<string> missing = new();

            foreach (string listName in MachineLocalLists.Concat(MachineLocalVars.Select(pair => pair.List)).Distinct())
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

        internal static void BoolPrefix(AGVar<bool> __instance, bool t, out Stamp __state)
        {
            __state = Open(GVarChangeMessage.KindBool, __instance.GetValue() ? 1 : 0, 0f, t ? 1 : 0, 0f);
        }

        internal static void BoolPostfix(AGVar<bool> __instance, Stamp __state)
        {
            try
            {
                NoteWhileConnecting(__instance);
                Close(__instance, __state, __instance.GetValue() ? 1 : 0, 0f);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[GVar] watching a bool change failed: " + e);
            }
        }

        internal static void IntPrefix(AGVar<int> __instance, int t, out Stamp __state)
        {
            __state = Open(GVarChangeMessage.KindInt, __instance.GetValue(), 0f, t, 0f);
        }

        internal static void IntPostfix(AGVar<int> __instance, Stamp __state)
        {
            try
            {
                NoteWhileConnecting(__instance);
                Close(__instance, __state, __instance.GetValue(), 0f);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[GVar] watching an int change failed: " + e);
            }
        }

        internal static void FloatPrefix(AGVar<float> __instance, float t, out Stamp __state)
        {
            __state = Open(GVarChangeMessage.KindFloat, 0, __instance.GetValue(), 0, t);
        }

        internal static void FloatPostfix(AGVar<float> __instance, Stamp __state)
        {
            try
            {
                NoteWhileConnecting(__instance);
                Close(__instance, __state, 0, __instance.GetValue());
            }
            catch (Exception e)
            {
                MelonLogger.Error("[GVar] watching a float change failed: " + e);
            }
        }

        internal static void QuestPrefix(AGVar<QuestState> __instance, QuestState t, out Stamp __state)
        {
            __state = Open(GVarChangeMessage.KindQuest, (int)__instance.GetValue(), 0f, (int)t, 0f);
        }

        internal static void QuestPostfix(AGVar<QuestState> __instance, Stamp __state)
        {
            try
            {
                NoteWhileConnecting(__instance);
                Close(__instance, __state, (int)__instance.GetValue(), 0f);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[GVar] watching a quest state change failed: " + e);
            }
        }

        // A write made while connecting is noted and told once the join's catch-up has landed (RemoteGVar).
        private static void NoteWhileConnecting(AGVarBase gvar)
        {
            if (CoopSession.State != CoopSessionState.Connecting) return;
            if (RemoteApply<GVarScope>.InProgress || AGVarBase.IsGVarLoading) return;

            GVarList? parent = gvar.GetParent();
            if (parent == null || string.IsNullOrEmpty(parent.Guid) || string.IsNullOrEmpty(gvar.Id)) return;
            if (GVarSyncAccess.IsLocal(parent.name, gvar.name)) return;

            RemoteGVar.NoteOwnWrite(gvar, parent.Guid, gvar.Id);
        }

        /// <summary>Tells the session a variable's present value, as a fresh write of it.</summary>
        internal static void ReportCurrent(AGVarBase gvar)
        {
            try
            {
                // Told even when this is what was told last: that telling may be the one that never arrived.
                GVarList? parent = gvar.GetParent();
                if (parent != null) LastAsserted.Remove(parent.Guid + "/" + gvar.Id);

                AGVar<bool>? b = gvar.TryCast<AGVar<bool>>();
                if (b != null)
                {
                    int v = b.GetValue() ? 1 : 0;
                    Close(b, Open(GVarChangeMessage.KindBool, v, 0f, v, 0f), v, 0f);
                    return;
                }

                AGVar<int>? i = gvar.TryCast<AGVar<int>>();
                if (i != null)
                {
                    int v = i.GetValue();
                    Close(i, Open(GVarChangeMessage.KindInt, v, 0f, v, 0f), v, 0f);
                    return;
                }

                AGVar<float>? f = gvar.TryCast<AGVar<float>>();
                if (f != null)
                {
                    float v = f.GetValue();
                    Close(f, Open(GVarChangeMessage.KindFloat, 0, v, 0, v), 0, v);
                    return;
                }

                AGVar<QuestState>? q = gvar.TryCast<AGVar<QuestState>>();
                if (q != null)
                {
                    int v = (int)q.GetValue();
                    Close(q, Open(GVarChangeMessage.KindQuest, v, 0f, v, 0f), v, 0f);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[GVar] telling a variable's present value failed: " + e);
            }
        }

        // Runs on every SetValue in the game, so it stays cheap and only stamps inside a session.
        private static Stamp Open(byte kind, int previousInt, float previousFloat, int intendedInt,
            float intendedFloat)
        {
            if (!CoopSession.SharesWorld || RemoteApply<GVarScope>.InProgress || AGVarBase.IsGVarLoading) return default;

            return new Stamp
            {
                Kind = kind,
                PreviousInt = previousInt,
                PreviousFloat = previousFloat,
                IntendedInt = intendedInt,
                IntendedFloat = intendedFloat,
                Sequence = ++_sequence,
            };
        }

        private static void Close(AGVarBase gvar, in Stamp stamp, int nowInt, float nowFloat)
        {
            if (stamp.Sequence == 0) return;

            // Re-checked because a listener of this very write can end the session or open a remote-apply scope.
            if (!CoopSession.SharesWorld) return;
            if (RemoteApply<GVarScope>.InProgress || AGVarBase.IsGVarLoading) return;

            GVarList? parent = gvar.GetParent();
            if (parent == null) return;

            if (GVarSyncAccess.IsLocal(parent.name, gvar.name)) return;

            string listGuid = parent.Guid;
            string varId = gvar.Id;
            if (string.IsNullOrEmpty(listGuid) || string.IsNullOrEmpty(varId)) return;

            // A local tally whose cause already crosses, so each machine counts for itself.
            if (GVarDerived.IsDerived(listGuid, varId)) return;

            // The game silently refuses writes while the active scene is not loaded.
            bool landed = stamp.Kind == GVarChangeMessage.KindFloat
                ? Math.Abs(nowFloat - stamp.IntendedFloat) < float.Epsilon
                : nowInt == stamp.IntendedInt;

            if (!landed)
            {
                RefusedLocally++;

                // Scene changes refuse every write by design, so only warn when writes are otherwise accepted.
                if (GVarEvents.IsWriteAccepted())
                {
                    MelonLogger.Warning($"[GVar] this world refused its own write to " +
                        $"'{parent.name}.{gvar.name}'; nothing was told to the session.");
                }

                return;
            }

            string key = listGuid + "/" + varId;
            long token = Token(stamp.Kind, nowInt, nowFloat);

            if (stamp.Kind == GVarChangeMessage.KindBool && stamp.PreviousInt != 0 && nowInt == 0)
            {
                LoweredAt[key] = UnityEngine.Time.unscaledTime;
            }

            if (LastAsserted.TryGetValue(key, out long said) && said == token) return;

            LastAsserted[key] = token;
            Reported++;

            if (Trace.Length > 0)
            {
                string name = parent.name + "." + gvar.name;

                if (name.Contains(Trace, StringComparison.OrdinalIgnoreCase))
                {
                    MelonLogger.Msg($"[GVar] asserted '{name}' = " +
                        $"{GVarChangeMessage.Describe(stamp.Kind, nowInt, nowFloat)} (was " +
                        $"{GVarChangeMessage.Describe(stamp.Kind, stamp.PreviousInt, stamp.PreviousFloat)}, seq {stamp.Sequence}).");
                }
            }

            GVarChangeMessage sent = new()
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
                Operation = GVarDerived.CurrentOperation ?? string.Empty,
            };

            RemoteGVar.NoteOwnWrite(gvar, listGuid, varId);
            RemoteGVar.ReportGVarChange(sent);
        }

        /// <summary>What the prefix hands the postfix. A struct because it is made on every variable write.</summary>
        internal struct Stamp
        {
            /// <summary>The order stamp, or zero when this write is not being watched.</summary>
            public uint Sequence;

            public byte Kind;

            public int PreviousInt;

            public float PreviousFloat;

            public int IntendedInt;

            public float IntendedFloat;
        }
    }
}
