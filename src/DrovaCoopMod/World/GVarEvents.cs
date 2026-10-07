using DrovaCoopMod.Messages;
using Il2CppDrova.GlobalVarSystem;
using Il2CppDrova.QuestSystem;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Writes a replicated global variable in two steps: a silent store, then its change event raised by hand.
    /// Quest gates, world observers and routines only wake on <c>ValueChangedEvent</c>, so a silent store alone is invisible.
    /// </summary>
    internal static class GVarEvents
    {
        /// <summary>
        /// Whether the game would accept a variable write now (<c>AGVar.SetValue</c> refuses silently otherwise).
        /// </summary>
        internal static bool IsWriteAccepted()
        {
            return UnityEngine.SceneManagement.SceneManager.GetActiveScene().isLoaded;
        }

        /// <summary>
        /// Reads a variable's current and authored initial value, boxed.
        /// </summary>
        /// <returns>False when the variable is none of the four synchronized types.</returns>
        internal static bool TryRead(AGVarBase gvar, out object value, out object initial)
        {
            value = null!;
            initial = null!;
            if (gvar == null) return false;

            AGVar<bool>? asBool = gvar.TryCast<AGVar<bool>>();
            if (asBool != null)
            {
                value = asBool.GetValue();
                initial = asBool.GetInitialValue();
                return true;
            }

            AGVar<int>? asInt = gvar.TryCast<AGVar<int>>();
            if (asInt != null)
            {
                value = asInt.GetValue();
                initial = asInt.GetInitialValue();
                return true;
            }

            AGVar<float>? asFloat = gvar.TryCast<AGVar<float>>();
            if (asFloat != null)
            {
                value = asFloat.GetValue();
                initial = asFloat.GetInitialValue();
                return true;
            }

            AGVar<QuestState>? asQuest = gvar.TryCast<AGVar<QuestState>>();
            if (asQuest != null)
            {
                value = asQuest.GetValue();
                initial = asQuest.GetInitialValue();
                return true;
            }

            return false;
        }

        /// <summary>
        /// The value a message carries, boxed in the type its kind names.
        /// </summary>
        /// <returns>False for a kind this build does not know.</returns>
        internal static bool TryReadReported(in GVarChangeMessage message, out object value)
        {
            switch (message.Kind)
            {
                case GVarChangeMessage.KindBool:
                    value = message.IntValue != 0;
                    return true;

                case GVarChangeMessage.KindInt:
                    value = message.IntValue;
                    return true;

                case GVarChangeMessage.KindFloat:
                    value = message.FloatValue;
                    return true;

                case GVarChangeMessage.KindQuest:
                    value = (QuestState)message.IntValue;
                    return true;

                default:
                    value = null!;
                    return false;
            }
        }

        /// <summary>
        /// Stores a value without running listeners and reads it back, since <c>SetValue</c> can drop a write silently.
        /// </summary>
        /// <returns>False when the type did not match or the game refused the write.</returns>
        internal static bool TryStoreSilently(AGVarBase gvar, object value)
        {
            if (gvar == null || value == null) return false;

            bool wasLoading = AGVarBase.IsGVarLoading;
            AGVarBase.IsGVarLoading = true;

            try
            {
                switch (value)
                {
                    case bool asBool:
                        AGVar<bool>? boolVar = gvar.TryCast<AGVar<bool>>();
                        if (boolVar == null) return false;

                        boolVar.SetValue(asBool, GVarContext.Empty);
                        return boolVar.GetValue() == asBool;

                    case int asInt:
                        AGVar<int>? intVar = gvar.TryCast<AGVar<int>>();
                        if (intVar == null) return false;

                        intVar.SetValue(asInt, GVarContext.Empty);
                        return intVar.GetValue() == asInt;

                    case float asFloat:
                        AGVar<float>? floatVar = gvar.TryCast<AGVar<float>>();
                        if (floatVar == null) return false;

                        floatVar.SetValue(asFloat, GVarContext.Empty);
                        return floatVar.GetValue() == asFloat;

                    case QuestState asQuest:
                        AGVar<QuestState>? questVar = gvar.TryCast<AGVar<QuestState>>();
                        if (questVar == null) return false;

                        questVar.SetValue(asQuest, GVarContext.Empty);
                        return questVar.GetValue() == asQuest;

                    default:
                        return false;
                }
            }
            finally
            {
                AGVarBase.IsGVarLoading = wasLoading;
            }
        }

        /// <summary>
        /// Raises the variable's <c>ValueChangedEvent</c> by hand, as though the game had just written it.
        /// Works for an unchanged value too, which repairs graphs that never heard of it but can re-run a trigger.
        /// </summary>
        /// <returns>False when the type did not match or the variable has no event channel.</returns>
        internal static bool TryReplay(AGVarBase gvar, object previous, object current)
        {
            if (gvar == null || current == null) return false;

            try
            {
                switch (current)
                {
                    case bool asBool:
                        AGVar<bool>? boolVar = gvar.TryCast<AGVar<bool>>();
                        if (boolVar?.ValueChangedEvent == null) return false;

                        boolVar.ValueChangedEvent.InvokeEvent(new AGVar<bool>.ChangeArgs(
                            asBool, previous is bool oldBool && oldBool, boolVar, GVarContext.Empty));
                        return true;

                    case int asInt:
                        AGVar<int>? intVar = gvar.TryCast<AGVar<int>>();
                        if (intVar?.ValueChangedEvent == null) return false;

                        intVar.ValueChangedEvent.InvokeEvent(new AGVar<int>.ChangeArgs(
                            asInt, previous is int oldInt ? oldInt : 0, intVar, GVarContext.Empty));
                        return true;

                    case float asFloat:
                        AGVar<float>? floatVar = gvar.TryCast<AGVar<float>>();
                        if (floatVar?.ValueChangedEvent == null) return false;

                        floatVar.ValueChangedEvent.InvokeEvent(new AGVar<float>.ChangeArgs(
                            asFloat, previous is float oldFloat ? oldFloat : 0f, floatVar, GVarContext.Empty));
                        return true;

                    case QuestState asQuest:
                        AGVar<QuestState>? questVar = gvar.TryCast<AGVar<QuestState>>();
                        if (questVar?.ValueChangedEvent == null) return false;

                        questVar.ValueChangedEvent.InvokeEvent(new AGVar<QuestState>.ChangeArgs(
                            asQuest, previous is QuestState oldQuest ? oldQuest : default, questVar,
                            GVarContext.Empty));
                        return true;

                    default:
                        return false;
                }
            }
            catch (Exception e)
            {
                // The value already landed. Swallowing keeps one bad listener from stopping the batch.
                MelonLogger.Error("[GVar] a listener threw while replaying a change: " + e);
                return false;
            }
        }

        /// <summary>
        /// Splits a boxed value into its wire kind and payload. <paramref name="intValue"/> carries every kind but float.
        /// </summary>
        /// <returns>False for a type that does not cross.</returns>
        internal static bool TryDescribe(object value, out byte kind, out int intValue, out float floatValue)
        {
            kind = 0;
            intValue = 0;
            floatValue = 0f;

            switch (value)
            {
                case bool asBool:
                    kind = GVarChangeMessage.KindBool;
                    intValue = asBool ? 1 : 0;
                    return true;

                case int asInt:
                    kind = GVarChangeMessage.KindInt;
                    intValue = asInt;
                    return true;

                case float asFloat:
                    kind = GVarChangeMessage.KindFloat;
                    floatValue = asFloat;
                    return true;

                case QuestState asQuest:
                    kind = GVarChangeMessage.KindQuest;
                    intValue = (int)asQuest;
                    return true;

                default:
                    return false;
            }
        }
    }
}
