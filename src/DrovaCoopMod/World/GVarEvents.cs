using DrovaCoopMod.Messages;
using Il2CppDrova.GlobalVarSystem;
using Il2CppDrova.QuestSystem;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Writes a global variable the way a replicated change has to be written: the value first, its
    /// consequences second, and each of them checkable.
    ///
    /// The game offers exactly two ways to write a variable and neither of them is what a co-op mod
    /// needs. A plain <c>SetValue</c> stores the value and runs every listener, which on the far
    /// machine means teleporting a player who was never there. A <c>SetValue</c> with
    /// <c>AGVarBase.IsGVarLoading</c> raised - the game's own save-restore path - stores the value and
    /// runs nothing at all, and that is what this mod used to do everywhere. It is worse than it
    /// sounds, because nothing in Drova polls a variable: quest-graph gates
    /// (<c>GraphGBoolCompService</c>), world observers (<c>GlobalConditionModule</c>) and NPC routines
    /// (<c>RoutineMapHandler</c>) all wake on <c>ValueChangedEvent</c> and cache between events. A
    /// value stored without its event is therefore invisible to the entire quest system, permanently -
    /// and worse still, <c>SetValue</c> returns early when the value is already what you are setting
    /// (<c>AGVar.cs:68-78</c>), so the player who later performs that beat for real produces no change,
    /// no event, and no repair. That is how both players could hold a quest whose stages never moved.
    ///
    /// So the two halves are separated here. The store uses the loading flag, because that is the only
    /// write the game will accept while a scene is mid-change (<c>AGVar.cs:79</c>) and the only one
    /// that cannot fire half a cascade. The consequences are then raised by hand, by invoking the same
    /// public <c>ValueChangedEvent</c> the game invokes, after every value of the batch has landed - so
    /// no listener ever reads a half-migrated world. Which of the consequences are allowed to run is
    /// not decided here; see <see cref="StoryEffectGate"/>.
    /// </summary>
    internal static class GVarEvents
    {
        /// <summary>
        /// Whether the game would accept a variable write at this moment.
        ///
        /// Restates <c>AGVar.SetValue</c>'s own precondition. It matters because <c>SetValue</c>
        /// returns <c>void</c>: when the game refuses a write there is no exception, no return code and
        /// no log line, and the previous build counted every refusal as a success.
        /// </summary>
        internal static bool IsWriteAccepted()
        {
            return UnityEngine.SceneManagement.SceneManager.GetActiveScene().isLoaded;
        }

        /// <summary>
        /// Reads a variable's current value, boxed, whatever its type.
        /// </summary>
        /// <param name="gvar">The variable.</param>
        /// <param name="value">Its value.</param>
        /// <returns>False when the variable is none of the four synchronized types.</returns>
        internal static bool TryRead(AGVarBase gvar, out object value)
        {
            value = null!;
            if (gvar == null) return false;

            AGVar<bool>? asBool = gvar.TryCast<AGVar<bool>>();
            if (asBool != null)
            {
                value = asBool.GetValue();
                return true;
            }

            AGVar<int>? asInt = gvar.TryCast<AGVar<int>>();
            if (asInt != null)
            {
                value = asInt.GetValue();
                return true;
            }

            AGVar<float>? asFloat = gvar.TryCast<AGVar<float>>();
            if (asFloat != null)
            {
                value = asFloat.GetValue();
                return true;
            }

            AGVar<QuestState>? asQuest = gvar.TryCast<AGVar<QuestState>>();
            if (asQuest != null)
            {
                value = asQuest.GetValue();
                return true;
            }

            return false;
        }

        /// <summary>
        /// The value a message carries, in the type its kind says it is.
        /// </summary>
        /// <param name="message">The assertion.</param>
        /// <param name="value">The boxed value.</param>
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
        /// Stores a value without running anything, and confirms it landed.
        ///
        /// The confirmation is the point. <c>SetValue</c> drops a write outright while the active scene
        /// is not loaded and says nothing about it, so a caller that does not read the value back
        /// cannot distinguish "applied" from "silently discarded" - which is exactly the distinction
        /// the previous build's <c>_applied</c> counter got wrong.
        /// </summary>
        /// <param name="gvar">The variable.</param>
        /// <param name="value">The value to store, boxed in its own type.</param>
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
        /// Runs a variable's consequences, as though the game had just written it.
        ///
        /// The same event <c>SetValue</c> raises (<c>AGVar.cs:87-90</c>), raised by hand on the same
        /// channel: <c>ValueChangedEvent</c> is public and resolves through <c>GetBaseGVar()</c> to the
        /// database's canonical copy, which is where every listener in the game subscribes. So a quest
        /// gate marks itself dirty, a world observer re-checks its condition, and an NPC's routine map
        /// goes stale - all of it the ordinary vanilla reaction, on a machine that was not there.
        ///
        /// It can be raised for a value that did not change, and that is deliberate rather than
        /// accidental: <c>AGVarCondition.TriggerConditionChangedEvent</c> re-checks unconditionally and
        /// the graph services only set a dirty flag, so re-firing repairs a world whose values are
        /// right and whose graphs never heard about them. Callers ask for that explicitly, because it
        /// can also re-run an authored trigger that already ran.
        /// </summary>
        /// <param name="gvar">The variable.</param>
        /// <param name="previous">What it held before, for the event's old-value field.</param>
        /// <param name="current">What it holds now.</param>
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
                // A listener of the far world threw. That is its business, not this write's: the value
                // has already landed, and swallowing here keeps one bad observer from stopping the rest
                // of the batch.
                MelonLogger.Error("[GVar] a listener threw while replaying a change: " + e);
                return false;
            }
        }

        /// <summary>
        /// The value as the wire carries it, for building an assertion out of a local write.
        /// </summary>
        /// <param name="value">The boxed value.</param>
        /// <param name="kind">Which of the message kinds it is.</param>
        /// <param name="intValue">The value for every kind but float.</param>
        /// <param name="floatValue">The value when the kind is float.</param>
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
