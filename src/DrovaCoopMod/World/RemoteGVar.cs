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
    /// Applies global-variable changes the session reported, through the game's own write path.
    ///
    /// The far half of <see cref="GVarWatch"/>. The variable is resolved the way the game resolves one,
    /// list guid to <c>GVarList</c> to id, and written with <c>SetValue</c>, so every listener on the
    /// far side reacts the way it did at home: the quest log updates, dialogue options open, the world
    /// reacts. A value already at the reported figure is a no-op inside SetValue itself, which is what
    /// makes the host's relay echo back to the sender harmless.
    ///
    /// Applied from the frame loop rather than where the message arrives, because the game may only be
    /// touched from there.
    /// </summary>
    internal static class RemoteGVar
    {
        /// <summary>
        /// How many writes one frame takes. A join snapshot delivers a whole playthrough's moved
        /// variables at once, and every write runs its listeners, so applying them all in one frame is
        /// a visible freeze right as the world appears.
        /// </summary>
        private const int MaxPerFrame = 64;

        private static int _applied;

        /// <summary>How many changes arrived and were written here. For <c>coop_status</c>.</summary>
        internal static int Applied => _applied;

        /// <summary>Forgets the count. Called when a session ends.</summary>
        internal static void Reset()
        {
            _applied = 0;
        }

        /// <summary>
        /// Applies everything that has arrived. Call once per frame from a loaded world.
        /// </summary>
        internal static void Update()
        {
            if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined) return;

            int budget = MaxPerFrame;

            while (budget-- > 0 && CoopSession.TryTakeGVarChange(out GVarChangeMessage message))
            {
                Apply(message);
            }
        }

        private static void Apply(in GVarChangeMessage message)
        {
            try
            {
                SubDatabase_GVars database = ProviderAccess.GVarDatabase;
                if (database == null) return;

                GVarList list = database.GetGVarListByGuid(message.ListGuid ?? string.Empty);
                if (list == null) return;

                AGVarBase gvar = list.GetGVarById(message.VarId ?? string.Empty);
                if (gvar == null) return;

                // Consulted on this side as well. The sender already refused to send anything marked
                // local, but the mark is per-machine configuration, and the machine that declared a
                // variable local is not necessarily the one that sent the change.
                if (GVarSyncAccess.IsLocal(list.name, gvar.name)) return;

                using (RemoteApply<GVarScope>.Enter())
                {
                    if (!Write(gvar, message)) return;
                }

                _applied++;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[GVar] applying a reported change failed: " + e);
            }
        }

        /// <summary>
        /// Writes the value onto the variable the kind says it is.
        /// </summary>
        /// <returns>False when the variable's type does not match the reported kind.</returns>
        private static bool Write(AGVarBase gvar, in GVarChangeMessage message)
        {
            switch (message.Kind)
            {
                case GVarChangeMessage.KindBool:
                    AGVar<bool>? asBool = gvar.TryCast<AGVar<bool>>();
                    if (asBool == null) return false;

                    asBool.SetValue(message.IntValue != 0, GVarContext.Empty);
                    return true;

                case GVarChangeMessage.KindInt:
                    AGVar<int>? asInt = gvar.TryCast<AGVar<int>>();
                    if (asInt == null) return false;

                    asInt.SetValue(message.IntValue, GVarContext.Empty);
                    return true;

                case GVarChangeMessage.KindFloat:
                    AGVar<float>? asFloat = gvar.TryCast<AGVar<float>>();
                    if (asFloat == null) return false;

                    asFloat.SetValue(message.FloatValue, GVarContext.Empty);
                    return true;

                case GVarChangeMessage.KindQuest:
                    AGVar<QuestState>? asQuest = gvar.TryCast<AGVar<QuestState>>();
                    if (asQuest == null) return false;

                    asQuest.SetValue((QuestState)message.IntValue, GVarContext.Empty);
                    return true;

                default:
                    return false;
            }
        }
    }
}
