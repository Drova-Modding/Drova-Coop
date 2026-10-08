using Il2CppDrova.JournalSystem;

namespace DrovaCoopMod.Systems.World.Patches
{
    /// <summary>The Harmony entry points of <see cref="JournalShare"/>. Each forwards to the system.</summary>
    internal static class JournalSharePatches
    {
        internal static bool AddEntryPrefix(Journal __instance, JournalEntry? entry, ref bool __result)
            => JournalShare.AddEntryPrefix(__instance, entry, ref __result);

        internal static void AddEntryPostfix(Journal __instance, JournalEntry? entry, bool __result)
            => JournalShare.AddEntryPostfix(__instance, entry, __result);

        internal static void ChangeTopicCategoryPostfix(Journal __instance, AJournalTopic? topic, JournalCategory? newCategory)
            => JournalShare.ChangeTopicCategoryPostfix(__instance, topic, newCategory);
    }
}
