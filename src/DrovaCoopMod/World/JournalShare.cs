using Drova_Modding_API;
using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Drova_Modding_API.Systems.Networking;
using Il2CppDrova;
using Il2CppDrova.JournalSystem;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Keeps every player's journal telling the same story.
    ///
    /// The journal is written by quest graphs, and a graph's progress lives in each machine's own
    /// scenes: it never crosses, so variable syncing alone leaves a joining player's journal empty no
    /// matter how loudly the values arrive. What does cross cleanly is the entries themselves. An
    /// entry is an authored asset the save already identifies by resource path, and triggering it is
    /// one call that deduplicates, pays the entry's rewards once, and autosaves. So entries cross as
    /// paths: live when one is earned, and as a walk of the whole journal when a player joins. This
    /// is also what carries a story-sequence quest's journal, whose variables never cross at all.
    /// </summary>
    internal static class JournalShare
    {
        /// <summary>How many entries one frame triggers. A join delivers a whole journal at once.</summary>
        private const int MaxPerFrame = 8;

        private static readonly List<string> _outgoing = new();

        /// <summary>
        /// Entry paths already in this journal this session, whether earned locally or applied from a
        /// teammate. The game's own trigger does not refuse a second identical add, and once both
        /// players run the same sequence graph each entry is earned on both machines and then relayed
        /// to the other - so the twin arriving a frame later would list the entry twice without this.
        /// </summary>
        private static readonly HashSet<string> _known = new(StringComparer.Ordinal);

        /// <summary>
        /// Category changes whose topic this journal does not hold yet. A checkmark can arrive ahead
        /// of the entries that create its topic, so it waits for them instead of being dropped.
        /// </summary>
        private static readonly List<(JournalTopicMessage Message, float Waited)> _pendingTopics = new();

        /// <summary>How long a checkmark waits for its topic before it is given up on.</summary>
        private const float TopicPatienceSeconds = 60f;

        private static int _shared;
        private static int _received;
        private static bool _warnedUnresolved;

        /// <summary>How many entries this machine told the session about. For <c>coop_status</c>.</summary>
        internal static int Shared => _shared;

        /// <summary>How many entries arrived and were written here. For <c>coop_status</c>.</summary>
        internal static int Received => _received;

        /// <summary>Forgets the counts. Called when a session ends.</summary>
        internal static void Reset()
        {
            _outgoing.Clear();
            _pendingTopics.Clear();
            _known.Clear();
            _shared = 0;
            _received = 0;
        }

        /// <summary>
        /// Applies the journal patches. Called once at melon init.
        ///
        /// The watch sits on AddEntryAsCategory, not AddEntry: AddEntry is a two-line wrapper the
        /// IL2CPP build inlines into its callers, so a patch on it applies and never fires -
        /// measured, not assumed, when a played dialogue's entry crossed to nobody.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPostfix(harmony, typeof(Journal), nameof(Journal.AddEntryAsCategory),
                typeof(JournalShare), nameof(AddEntryPostfix));
            Hooking.TryPostfix(harmony, typeof(Journal), nameof(Journal.ChangeTopicCategory),
                typeof(JournalShare), nameof(ChangeTopicCategoryPostfix));
        }

        /// <summary>
        /// Sends every entry this journal holds to one peer. Called by the host when a player joins.
        /// </summary>
        internal static void SendTo(INetPeer peer)
        {
            try
            {
                if (!TryGetJournal(out Journal journal)) return;

                Il2CppSystem.Collections.Generic.Dictionary<AJournalTopic, Journal_RuntimeTopic> topics =
                    journal._topicLookUp;

                if (topics == null) return;

                List<string> paths = new();
                Il2CppSystem.Collections.Generic.List<JournalEntry> entries = new();

                foreach (Il2CppSystem.Collections.Generic.KeyValuePair<AJournalTopic, Journal_RuntimeTopic> pair
                    in topics)
                {
                    Journal_RuntimeTopic topic = pair.Value;
                    if (topic == null) continue;

                    entries.Clear();
                    topic.GetEntries(ref entries);

                    for (int index = 0; index < entries.Count; index++)
                    {
                        string path = entries[index]?.GetResourcesPath();
                        if (string.IsNullOrEmpty(path)) continue;

                        paths.Add(path!);
                    }
                }

                for (int start = 0; start < paths.Count; start += JournalEntryMessage.MaxPaths)
                {
                    int count = Math.Min(JournalEntryMessage.MaxPaths, paths.Count - start);

                    string[] chunk = new string[count];
                    paths.CopyTo(start, chunk, 0, count);

                    NetworkAccess.Send(peer, new JournalEntryMessage { Paths = chunk });
                }

                // The checkmarks, after the entries that create their topics. Only topics that left
                // their authored default category are worth a message.
                int marks = 0;

                foreach (Il2CppSystem.Collections.Generic.KeyValuePair<AJournalTopic, Journal_RuntimeTopic> pair
                    in topics)
                {
                    Journal_RuntimeTopic topic = pair.Value;
                    if (topic == null || topic.Category == null || pair.Key == null) continue;

                    if (topic.Category.Pointer == pair.Key.GetRelatedDefaultCategory()?.Pointer) continue;

                    string topicPath = pair.Key.GetResourcesPath();
                    string categoryPath = topic.Category.GetResourcesPath();
                    if (string.IsNullOrEmpty(topicPath) || string.IsNullOrEmpty(categoryPath)) continue;

                    marks++;

                    NetworkAccess.Send(peer, new JournalTopicMessage
                    {
                        TopicPath = topicPath,
                        CategoryPath = categoryPath,
                    });
                }

                if (paths.Count > 0 || marks > 0)
                {
                    MelonLogger.Msg($"[Journal] a joining player was sent {paths.Count} journal " +
                        $"entr(ies) and {marks} completion mark(s), so their journal tells this " +
                        "world's story.");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Journal] walking the journal for a joining player failed: " + e);
            }
        }

        /// <summary>
        /// Triggers everything that has arrived. Call once per frame from a loaded world.
        /// </summary>
        internal static void Update()
        {
            if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined)
            {
                Reset();
                return;
            }

            int budget = MaxPerFrame;

            while (budget > 0 && CoopSession.TryTakeJournal(out JournalEntryMessage message))
            {
                if (message.Paths == null) continue;

                foreach (string path in message.Paths)
                {
                    if (string.IsNullOrEmpty(path)) continue;

                    budget--;
                    Apply(path);
                }
            }

            while (CoopSession.TryTakeJournalTopic(out JournalTopicMessage mark))
            {
                _pendingTopics.Add((mark, 0f));
            }

            for (int index = _pendingTopics.Count - 1; index >= 0; index--)
            {
                (JournalTopicMessage mark, float waited) = _pendingTopics[index];

                if (ApplyTopic(mark))
                {
                    _pendingTopics.RemoveAt(index);
                    continue;
                }

                waited += UnityEngine.Time.unscaledDeltaTime;

                if (waited >= TopicPatienceSeconds)
                {
                    _pendingTopics.RemoveAt(index);
                    continue;
                }

                _pendingTopics[index] = (mark, waited);
            }
        }

        /// <summary>
        /// Moves a topic to its reported category, once the topic exists here.
        /// </summary>
        /// <returns>True when it landed or can never land; false to keep waiting for the topic.</returns>
        private static bool ApplyTopic(in JournalTopicMessage mark)
        {
            try
            {
                if (string.IsNullOrEmpty(mark.TopicPath) || string.IsNullOrEmpty(mark.CategoryPath)) return true;
                if (!TryGetJournal(out Journal journal)) return false;

                AJournalTopic topic = UnityEngine.Resources.Load<AJournalTopic>(mark.TopicPath);
                JournalCategory category = UnityEngine.Resources.Load<JournalCategory>(mark.CategoryPath);
                if (topic == null || category == null) return true;

                // Not there yet: its entries may still be in the queue. Waited for rather than
                // forced, because ChangeTopicCategory on an absent topic only logs a warning.
                if (journal._topicLookUp == null || !journal._topicLookUp.ContainsKey(topic)) return false;

                using (RemoteApply<JournalScope>.Enter())
                {
                    journal.ChangeTopicCategory(topic, category);
                }

                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Journal] applying a completion mark failed: " + e);
                return true;
            }
        }

        private static void Apply(string path)
        {
            try
            {
                // A cheap first answer, and only that. It is a cache of what this session has said or
                // heard, so it cannot know about entries the journal held before the session began.
                if (_known.Contains(path)) return;

                // The authoritative one. The game's own duplicate check compares entry objects by
                // reference (Journal_RuntimeTopic walks _entries testing == entry), and the object
                // Resources.Load hands back for a path is NOT the object a quest graph holds a
                // reference to - the graph's comes from the asset bundle, this one from
                // resources.assets. So the game's check passes, the entry is added a second time, and
                // JournalEntry.Trigger pays its experience reward again. Comparing by resource path
                // is the same question asked in a way that survives two copies of one asset.
                if (IsInJournal(path))
                {
                    _known.Add(path);
                    return;
                }

                JournalEntry entry = UnityEngine.Resources.Load<JournalEntry>(path);

                if (entry == null)
                {
                    if (!_warnedUnresolved)
                    {
                        _warnedUnresolved = true;
                        MelonLogger.Warning($"[Journal] no entry lives at '{path}'; the game versions " +
                            "differ. Later misses are only counted.");
                    }

                    return;
                }

                using (RemoteApply<JournalScope>.Enter())
                {
                    entry.Trigger(null);
                }

                // Only now. Remembering the path before the trigger meant that an entry whose trigger
                // did not take - a journal that was not ready yet - was blacklisted for the rest of
                // the session and never arrived at all.
                _known.Add(path);
                _received++;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Journal] triggering a shared entry failed: " + e);
            }
        }

        /// <summary>
        /// Whether this journal already holds an entry with that resource path.
        /// </summary>
        /// <param name="path">The entry's resource path, which is the identity the save stores.</param>
        private static bool IsInJournal(string path)
        {
            if (!TryGetJournal(out Journal journal)) return false;

            Il2CppSystem.Collections.Generic.Dictionary<AJournalTopic, Journal_RuntimeTopic> topics =
                journal._topicLookUp;

            if (topics == null) return false;

            Il2CppSystem.Collections.Generic.List<JournalEntry> entries = new();

            foreach (Il2CppSystem.Collections.Generic.KeyValuePair<AJournalTopic, Journal_RuntimeTopic> pair
                in topics)
            {
                Journal_RuntimeTopic topic = pair.Value;
                if (topic == null) continue;

                entries.Clear();
                topic.GetEntries(ref entries);

                for (int index = 0; index < entries.Count; index++)
                {
                    if (entries[index]?.GetResourcesPath() == path) return true;
                }
            }

            return false;
        }

        private static void AddEntryPostfix(Journal __instance, JournalEntry entry, bool __result)
        {
            try
            {
                if (!__result) return;
                if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined) return;
                if (RemoteApply<JournalScope>.InProgress) return;

                // A save load replays every stored entry through AddEntry; those are this journal's
                // own history, not news.
                if (__instance != null && __instance.IsLoadingInProgress) return;

                string path = entry?.GetResourcesPath();
                if (string.IsNullOrEmpty(path)) return;

                // Remembered so the teammate's copy of this same entry, relayed back a frame later
                // once both machines run the sequence graph, is refused instead of listed twice.
                _known.Add(path!);

                _shared++;
                CoopSession.ReportJournalEntry(path!);

                // Release, not Debug. Journal traffic is tens of lines in a whole session, and the
                // absence of this line in a release build cost a full investigation strand: there was
                // no way to tell a journal that shared nothing from one whose sharing was not compiled.
                MelonLogger.Msg($"[Journal] '{path}' was earned here and told to the session.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Journal] reporting an earned entry failed: " + e);
            }
        }

        private static void ChangeTopicCategoryPostfix(Journal __instance, AJournalTopic topic,
            JournalCategory newCategory)
        {
            try
            {
                if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined) return;
                if (RemoteApply<JournalScope>.InProgress) return;
                if (__instance != null && __instance.IsLoadingInProgress) return;

                string topicPath = topic?.GetResourcesPath();
                string categoryPath = newCategory?.GetResourcesPath();
                if (string.IsNullOrEmpty(topicPath) || string.IsNullOrEmpty(categoryPath)) return;

                CoopSession.ReportJournalTopic(new JournalTopicMessage
                {
                    TopicPath = topicPath!,
                    CategoryPath = categoryPath!,
                });
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Journal] reporting a completion mark failed: " + e);
            }
        }

        private static bool TryGetJournal(out Journal journal)
        {
            journal = null!;

            if (!PlayerMetaDataGameHandler.TryGet(out PlayerMetaDataGameHandler handler) || handler == null)
            {
                return false;
            }

            Journal found = handler.GetJournal();
            if (found == null) return false;

            journal = found;
            return true;
        }
    }
}
