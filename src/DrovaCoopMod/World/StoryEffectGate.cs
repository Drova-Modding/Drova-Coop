using Drova_Modding_API;
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
    /// world: five classes off, everything else on.
    ///
    /// Three of them can actually be gated this way, and they are the three that react to the change
    /// event, which is the window this class owns. The other two - <c>GlobalObserverBhvr_SceneSwitch</c>
    /// and <c>GlobalObserverBhvr_StartDialogue</c> - poll their condition from <c>Update</c> instead of
    /// listening, so they read the stored value on some later frame when no window is open. A window
    /// cannot stop them and pretending otherwise would be a guard that reads like protection and is
    /// not one. They are unchanged by this work: the value already crossed and was already stored
    /// before it, so they already behaved this way. <c>StartDialogue</c> in particular wants the same
    /// treatment a scripted brawl gets - a lease, first machine to trigger owns it - and that is its
    /// own piece of work.
    /// </summary>
    internal static class StoryEffectGate
    {
        [ThreadStatic]
        private static int _depth;

        private static int _suppressed;
        private static int _portsHeld;
        private static bool _installed;

        /// <summary>How many player-body effects were refused. For <c>coop_status</c>.</summary>
        internal static int Suppressed => _suppressed;

        /// <summary>
        /// How many of those were the authored teleport that moves the player into a story.
        ///
        /// Counted apart from the rest because it is the one suppression that has to be answered:
        /// refusing it is right - the story ported somebody else - but a capture is meant to take
        /// everyone, so the mod has to move this player deliberately instead. This counter is the
        /// signal that it should. It is a far better trigger than the one it replaces, which was a
        /// hand-written list of which variables count as a story gate, per quest.
        /// </summary>
        internal static int PortsHeld => _portsHeld;

        /// <summary>Whether the prefixes are in place at all.</summary>
        internal static bool Installed => _installed;

        /// <summary>Forgets the count. Called when a session ends.</summary>
        internal static void Reset()
        {
            _suppressed = 0;
        }

        /// <summary>
        /// Applies the prefixes. Called once at melon init.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            int applied = 0;

            if (Hooking.TryPrefix(harmony, typeof(Il2CppDrova.GlobalVarsSystem.GlobalObserver_TeleportPlayer),
                "ConditionChangedListener", typeof(StoryEffectGate), nameof(SkipPortWhileSuppressed)))
            {
                applied++;
            }

            if (Hooking.TryPrefix(harmony, typeof(Il2CppDrova.GlobalVarSystem.GlobalObserverBhvr_ChangePlayerInventory),
                "ConditionChangedListener", typeof(StoryEffectGate), nameof(SkipWhileSuppressed)))
            {
                applied++;
            }

            if (Hooking.TryPrefix(harmony, typeof(Il2CppDrova.GlobalVarSystem.GlobalObserverBhvr_WavesSpawning),
                "OnConditionChangedEventListener", typeof(StoryEffectGate), nameof(SkipWhileSuppressed)))
            {
                applied++;
            }

            _installed = applied > 0;

            // Said out loud because the whole apply path is built on the assumption that these three
            // hold. If they did not apply - a game update moved them, IL2CPP inlined one - then
            // replaying a change event can teleport this player into a teammate's story, and that is
            // worth knowing before it happens rather than afterwards.
            if (applied == 3)
            {
                MelonLogger.Msg("[GVar] player-body effect gate installed on 3 observers; a teammate's " +
                    "story can move this world without moving this player.");
            }
            else
            {
                MelonLogger.Warning($"[GVar] player-body effect gate installed on {applied} of 3 " +
                    "observers. Replicated story variables may act on this player directly. Run " +
                    "coop_status to see how often the gate fires.");
            }
        }

        /// <summary>
        /// Opens the window in which a player-body effect is refused. Dispose it to close.
        /// </summary>
        internal static Scope Suppress()
        {
            _depth++;
            return default;
        }

        private static bool SkipWhileSuppressed()
        {
            if (_depth <= 0) return true;

            _suppressed++;
            return false;
        }

        private static bool SkipPortWhileSuppressed()
        {
            if (_depth <= 0) return true;

            _suppressed++;
            _portsHeld++;
            return false;
        }

        /// <summary>
        /// The open window. A struct, so opening it allocates nothing.
        /// </summary>
        internal readonly struct Scope : IDisposable
        {
            /// <summary>Closes the window.</summary>
            public void Dispose()
            {
                if (_depth > 0)
                {
                    _depth--;
                }
            }
        }
    }
}
