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
    /// Watches every global variable this machine changes, and tells the session.
    ///
    /// <c>AGVar&lt;T&gt;.SetValue</c> is the funnel: every quest advance, dialogue consequence and world
    /// flag goes through it, and each closed generic is its own native method, so all four are patched.
    /// The patch pair captures the value before the write and compares after, because SetValue is called
    /// far more often than values change and only a change is news.
    ///
    /// What does not cross: writes made while a save is streaming in (<c>AGVarBase.IsGVarLoading</c>),
    /// writes this mod itself applied from the wire, and variables a mod has declared local through
    /// <see cref="GVarSyncAccess"/> - per-player state like a class choice, which two players need not
    /// share.
    /// </summary>
    internal static class GVarWatch
    {
        // A scene's copy of a variable delegates its write to the database's canonical copy, and both
        // calls run through the patch. Only the outermost call reports, or one change crosses twice.
        [ThreadStatic]
        private static int _depth;

        private static int _reported;

        /// <summary>How many changes were told to the session. For <c>coop_status</c>.</summary>
        internal static int Reported => _reported;

        /// <summary>Forgets the count. Called when a session ends.</summary>
        internal static void Reset()
        {
            _reported = 0;
        }

        /// <summary>
        /// Applies the patches. Called once at melon init.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
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

        private static void BoolPrefix(AGVar<bool> __instance, out bool __state)
        {
            _depth++;
            __state = __instance.GetValue();
        }

        private static void BoolPostfix(AGVar<bool> __instance, bool __state)
        {
            try
            {
                if (_depth > 0) _depth--;
                if (_depth > 0) return;

                bool now = __instance.GetValue();
                if (now == __state) return;

                Report(__instance, GVarChangeMessage.KindBool, now ? 1 : 0, 0f);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[GVar] watching a bool change failed: " + e);
            }
        }

        private static void IntPrefix(AGVar<int> __instance, out int __state)
        {
            _depth++;
            __state = __instance.GetValue();
        }

        private static void IntPostfix(AGVar<int> __instance, int __state)
        {
            try
            {
                if (_depth > 0) _depth--;
                if (_depth > 0) return;

                int now = __instance.GetValue();
                if (now == __state) return;

                Report(__instance, GVarChangeMessage.KindInt, now, 0f);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[GVar] watching an int change failed: " + e);
            }
        }

        private static void FloatPrefix(AGVar<float> __instance, out float __state)
        {
            _depth++;
            __state = __instance.GetValue();
        }

        private static void FloatPostfix(AGVar<float> __instance, float __state)
        {
            try
            {
                if (_depth > 0) _depth--;
                if (_depth > 0) return;

                float now = __instance.GetValue();
                if (now == __state) return;

                Report(__instance, GVarChangeMessage.KindFloat, 0, now);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[GVar] watching a float change failed: " + e);
            }
        }

        private static void QuestPrefix(AGVar<QuestState> __instance, out QuestState __state)
        {
            _depth++;
            __state = __instance.GetValue();
        }

        private static void QuestPostfix(AGVar<QuestState> __instance, QuestState __state)
        {
            try
            {
                if (_depth > 0) _depth--;
                if (_depth > 0) return;

                QuestState now = __instance.GetValue();
                if (now == __state) return;

                Report(__instance, GVarChangeMessage.KindQuest, (int)now, 0f);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[GVar] watching a quest state change failed: " + e);
            }
        }

        /// <summary>
        /// Tells the session about one change that actually happened.
        ///
        /// Ordered cheapest first, because the prefix and postfix run on every SetValue in the game.
        /// The loading gate matters most: a save streaming in writes hundreds of variables, and every
        /// one of them is this machine catching up rather than news.
        /// </summary>
        private static void Report(AGVarBase gvar, byte kind, int intValue, float floatValue)
        {
            if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined) return;
            if (RemoteApply<GVarScope>.InProgress) return;
            if (AGVarBase.IsGVarLoading) return;

            GVarList parent = gvar.GetParent();
            if (parent == null) return;

            if (GVarSyncAccess.IsLocal(parent.name, gvar.name)) return;

            string listGuid = parent.Guid;
            string varId = gvar.Id;
            if (string.IsNullOrEmpty(listGuid) || string.IsNullOrEmpty(varId)) return;

            _reported++;

            CoopSession.ReportGVarChange(new GVarChangeMessage
            {
                ListGuid = listGuid,
                VarId = varId,
                Kind = kind,
                IntValue = intValue,
                FloatValue = floatValue,
            });
        }
    }
}
