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
    /// Keeps every player's journal telling the same story. Quest graphs never cross,
    /// so entries cross as resource paths, live and as a full walk when a player joins.
    /// </summary>
    internal static class JournalShare
    {
        /// <summary>Entries triggered per frame. A join delivers a whole journal at once.</summary>
        private const int MaxPerFrame = 8;

        private static readonly List<string> Outgoing = new();

        /// <summary>Entry paths already in this journal this session, since the game's trigger accepts a second identical add.</summary>
        private static readonly HashSet<string> Known = new(StringComparer.Ordinal);

        /// <summary>Category changes waiting for their topic, since a checkmark can arrive before the entries that create it.</summary>
        private static readonly List<(JournalTopicMessage Message, float Waited)> PendingTopics = new();

        /// <summary>How long a checkmark waits for its topic.</summary>
        private const float TopicPatienceSeconds = 60f;

        private static bool _warnedUnresolved;

        /// <summary>How many entries this machine told the session about. For <c>coop_status</c>.</summary>
        internal static int Shared { get; private set; }

        /// <summary>How many entries arrived and were written here. For <c>coop_status</c>.</summary>
        internal static int Received { get; private set; }

        /// <summary>Duplicate entries refused. For <c>coop_status</c>.</summary>
        internal static int RefusedDuplicates { get; private set; }

        /// <summary>Forgets the counts. Called when a session ends.</summary>
        internal static void Reset()
        {
            Outgoing.Clear();
            PendingTopics.Clear();
            Known.Clear();
            Shared = 0;
            Received = 0;
            RefusedDuplicates = 0;
        }

        /// <summary>
        /// Applies the journal patches. Called once at melon init. Hooks AddEntryAsCategory because AddEntry is inlined.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            // The game's duplicate check compares entry objects, and Resources.Load's copy is not the bundle's.
            // Comparing by path has to sit in front of the game's add, since either side can arrive second.
            Hooking.TryPrefix(harmony, typeof(Journal), nameof(Journal.AddEntryAsCategory),
                typeof(JournalShare), nameof(AddEntryPrefix));

            Hooking.TryPostfix(harmony, typeof(Journal), nameof(Journal.AddEntryAsCategory),
                typeof(JournalShare), nameof(AddEntryPostfix));
            Hooking.TryPostfix(harmony, typeof(Journal), nameof(Journal.ChangeTopicCategory),
                typeof(JournalShare), nameof(ChangeTopicCategoryPostfix));
        }

        /// <summary>Sends every entry this journal holds to one peer. Called by the host when a player joins.</summary>
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

                // Checkmarks after the entries that create their topics, only for topics off their authored default.
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

        /// <summary>Triggers everything that has arrived. Call once per frame from a loaded world.</summary>
        internal static void Update()
        {
            if (!CoopSession.IsLive)
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
                PendingTopics.Add((mark, 0f));
            }

            // Oldest first, so two waiting marks for one topic apply in order.
            for (int index = 0; index < PendingTopics.Count; index++)
            {
                (JournalTopicMessage mark, float waited) = PendingTopics[index];

                if (ApplyTopic(mark))
                {
                    PendingTopics.RemoveAt(index--);
                    continue;
                }

                waited += UnityEngine.Time.unscaledDeltaTime;

                if (waited >= TopicPatienceSeconds)
                {
                    PendingTopics.RemoveAt(index--);
                    continue;
                }

                PendingTopics[index] = (mark, waited);
            }
        }

        /// <summary>Moves a topic to its reported category, once the topic exists here.</summary>
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

                // Not there yet, its entries may still be queued. ChangeTopicCategory on an absent topic only warns.
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
                // A session cache only. It knows nothing the journal held before the session began.
                if (Known.Contains(path)) return;

                // Authoritative: by path, since the game's reference check misses the second asset copy and would pay the reward again.
                if (IsInJournal(path))
                {
                    Known.Add(path);
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

                // Only after the trigger, so an entry whose trigger did not take stays deliverable.
                Known.Add(path);
                Received++;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Journal] triggering a shared entry failed: " + e);
            }
        }

        /// <summary>Whether this journal already holds an entry with that resource path.</summary>
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

        /// <summary>Refuses an entry this journal already holds, whichever copy of the asset it arrives as.</summary>
        /// <returns>False to skip the add entirely.</returns>
        private static bool AddEntryPrefix(Journal __instance, JournalEntry entry, ref bool __result)
        {
            try
            {
                // A save load replays this world's own history through here.
                if (__instance != null && __instance.IsLoadingInProgress) return true;

                string path = entry?.GetResourcesPath();
                if (string.IsNullOrEmpty(path)) return true;

                if (!IsInJournal(path!)) return true;

                RefusedDuplicates++;

                MelonLogger.Msg($"[Journal] '{path}' is already in this journal; the second copy was " +
                    "refused. Its rewards are not paid twice.");

                __result = false;
                return false;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Journal] checking an entry for a duplicate failed: " + e);
                return true;
            }
        }

        private static void AddEntryPostfix(Journal __instance, JournalEntry entry, bool __result)
        {
            try
            {
                if (!__result) return;
                if (!CoopSession.IsLive) return;
                if (RemoteApply<JournalScope>.InProgress) return;

                // A save load replays stored entries through AddEntry. Not news.
                if (__instance != null && __instance.IsLoadingInProgress) return;

                string path = entry?.GetResourcesPath();
                if (string.IsNullOrEmpty(path)) return;

                // So the teammate's relayed copy of this entry is refused instead of listed twice.
                Known.Add(path!);

                Shared++;
                CoopSession.ReportJournalEntry(path!);

                // Release-level on purpose: journal traffic is rare, and its absence must be distinguishable from no sharing.
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
                if (!CoopSession.IsLive) return;
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
