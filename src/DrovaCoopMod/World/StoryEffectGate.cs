using Drova_Modding_API;
using DrovaCoopMod.Sync;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Keeps a replicated story from happening <em>to</em> the player it did not happen to.
    ///
    /// Replaying a variable's change event on the far machine is what makes its world react, and
    /// almost all of that reaction is exactly what co-op wants: NPCs get ported to their staged
    /// places, actors change faction, doors unlock, routines go stale, quest gates open, dialogue
    /// options appear. Roughly forty observer classes do that kind of work and not one of them has
    /// ever run from a teammate's action, because the old apply path suppressed the event entirely.
    ///
    /// A handful do something else: they act on <em>this</em> machine's player body. Those must not
    /// run, because the story they describe happened to somebody else - and the mod already
    /// replicates the player-facing half deliberately, on its own channels (cutscenes, items,
    /// experience, journal entries, the gate follow). So the rule is to suppress the body, not the
    /// world: those classes off, everything else on.
    ///
    /// One is gated here: the story's swap of the player's own inventory, which reacts to the change
    /// event, the window this class owns. Wave spawns used to be as well, which left the far machine's
    /// wave fight dead until a reload; <see cref="StorySceneLease"/> pauses them instead. The authored teleport observer this class used to gate
    /// ships no instance at all - every story port is a teleport action inside a dialogue cutscene,
    /// handled by <see cref="GateFollow"/> - and the scene-switch observer ships none either.
    /// <c>GlobalObserverBhvr_StartDialogue</c> polls from <c>Update</c>, so no window can stop it;
    /// <see cref="StorySceneLease"/> decides where it runs instead.
    /// </summary>
    internal static class StoryEffectGate
    {
        /// <summary>How many player-body effects were refused. For <c>coop_status</c>.</summary>
        internal static int Suppressed { get; private set; }

        /// <summary>Whether the prefixes are in place at all.</summary>
        internal static bool Installed { get; private set; }

        /// <summary>Forgets the count. Called when a session ends.</summary>
        internal static void Reset()
        {
            Suppressed = 0;
        }

        /// <summary>
        /// Applies the prefixes. Called once at melon init.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            int applied = 0;

            if (Hooking.TryPrefix(harmony, typeof(Il2CppDrova.GlobalVarSystem.GlobalObserverBhvr_ChangePlayerInventory),
                "ConditionChangedListener", typeof(StoryEffectGate), nameof(SkipWhileSuppressed)))
            {
                applied++;
            }

            Installed = applied > 0;

            // Said out loud because the whole apply path is built on the assumption that this one
            // holds. If they did not apply - a game update moved them, IL2CPP inlined one - then
            // replaying a change event can teleport this player into a teammate's story, and that is
            // worth knowing before it happens rather than afterwards.
            if (applied == 1)
            {
                MelonLogger.Msg("[GVar] player-body effect gate installed on 1 observer; a teammate's " +
                    "story can move this world without moving this player.");
            }
            else
            {
                MelonLogger.Warning($"[GVar] player-body effect gate installed on {applied} of 1 " +
                    "observers. Replicated story variables may act on this player directly. Run " +
                    "coop_status to see how often the gate fires.");
            }
        }

        private static bool SkipWhileSuppressed()
        {
            if (!RemoteApply<StoryScope>.InProgress) return true;

            Suppressed++;
            return false;
        }
    }

    /// <summary>
    /// The window in which <see cref="StoryEffectGate"/> refuses a player-body effect: a replicated
    /// variable's change event being replayed here.
    /// </summary>
    internal readonly struct StoryScope : IApplyScope
    {
    }
}
