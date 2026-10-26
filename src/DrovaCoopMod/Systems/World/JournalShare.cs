using System.Diagnostics.CodeAnalysis;
using Drova_Modding_API;
using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using Drova_Modding_API.Systems.Quests;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Sync;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.Systems.World.Patches;
using Il2CppDrova;
using Il2CppDrova.JournalSystem;
using MelonLoader;

namespace DrovaCoopMod.Systems.World
{
    /// <summary>
    /// Keeps every player's journal telling the same story. Quest graphs never cross,
    /// so entries cross as resource paths, live and as a full walk when a player joins.
    /// </summary>
    internal sealed class JournalShare : ICoopSystem
    {
        /// <summary>Entries triggered per frame. A join delivers a whole journal at once.</summary>
        private const int MaxPerFrame = 8;

        /// <summary>How long a checkmark waits for its topic.</summary>
        private const float TopicPatienceSeconds = 60f;

        /// <summary>Entry paths already in this journal this session, since the game's trigger accepts a second identical add.</summary>
        private static readonly HashSet<string> Known = new(StringComparer.Ordinal);

        /// <summary>Category changes waiting for their topic, since a checkmark can arrive before the entries that create it.</summary>
        private static readonly List<(JournalTopicMessage Message, float Waited)> PendingTopics = new();

        private static readonly Queue<JournalEntryMessage> IncomingJournal = new();
        private static readonly Queue<JournalTopicMessage> IncomingJournalTopics = new();

        private static bool _warnedUnresolved;

        private static bool _knownSeeded;

        /// <summary>How many entries this machine told the session about. For <c>coop_status</c>.</summary>
        internal static int Shared { get; private set; }

        /// <summary>How many entries arrived and were written here. For <c>coop_status</c>.</summary>
        internal static int Received { get; private set; }

        /// <summary>Duplicate entries refused. For <c>coop_status</c>.</summary>
        internal static int RefusedDuplicates { get; private set; }

        /// <summary>Forgets the counts. Called when a session ends.</summary>
        private static void Reset()
        {
            PendingTopics.Clear();
            Known.Clear();
            _knownSeeded = false;
            Shared = 0;
            Received = 0;
            RefusedDuplicates = 0;
        }

        /// <summary>
        /// Applies the journal patches. Called once at melon init. Hooks AddEntryAsCategory because AddEntry is inlined.
        /// </summary>
        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            // The game's duplicate check compares entry objects, and Resources.Load's copy is not the bundle's.
            // Comparing by path has to sit in front of the game's add, since either side can arrive second.
            Hooking.TryPrefix(harmony, typeof(Journal), nameof(Journal.AddEntryAsCategory),
                typeof(JournalSharePatches), nameof(JournalSharePatches.AddEntryPrefix));

            Hooking.TryPostfix(harmony, typeof(Journal), nameof(Journal.AddEntryAsCategory),
                typeof(JournalSharePatches), nameof(JournalSharePatches.AddEntryPostfix));
            Hooking.TryPostfix(harmony, typeof(Journal), nameof(Journal.ChangeTopicCategory),
                typeof(JournalSharePatches), nameof(JournalSharePatches.ChangeTopicCategoryPostfix));
        }

        /// <summary>Sends every entry this journal holds to one peer. Called by the host when a player joins.</summary>
        internal static void SendTo(INetPeer peer)
        {
            try
            {
                if (!TryGetJournal(out Journal? journal)) return;

                Il2CppSystem.Collections.Generic.Dictionary<AJournalTopic, Journal_RuntimeTopic> topics =
                    journal._topicLookUp;

                if (topics == null) return;

                List<string> paths = new();
                CollectPaths(topics, paths);

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

                    JournalCategory? authored = pair.Key.GetRelatedDefaultCategory();
                    if (authored != null && topic.Category.Pointer == authored.Pointer) continue;

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

        /// <summary>Triggers everything that has arrived.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (!CoopSession.SharesWorld)
            {
                Reset();
                return;
            }

            int budget = MaxPerFrame;

            while (budget > 0 && TryTakeJournal(out JournalEntryMessage message))
            {
                if (message.Paths == null) continue;

                foreach (string path in message.Paths)
                {
                    if (string.IsNullOrEmpty(path)) continue;

                    budget--;
                    Apply(path);
                }
            }

            while (TryTakeJournalTopic(out JournalTopicMessage mark))
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
                if (!TryGetJournal(out Journal? journal)) return false;

                // A custom quest's topic is not a resource; the API resolves it by the same path.
                AJournalTopic? topic = UnityEngine.Resources.Load<AJournalTopic>(mark.TopicPath);
                if (topic == null) topic = QuestSystem.FindJournalTopic(mark.TopicPath);
                JournalCategory? category =UnityEngine.Resources.Load<JournalCategory>(mark.CategoryPath);
                if (topic == null || category == null) return true;

                // Its entries may still be queued.
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
                // Seeded by path once: the game's reference check misses the second asset copy and would pay the reward again.
                if (!_knownSeeded && TryGetJournal(out Journal? journal) && !journal.IsLoadingInProgress && journal._topicLookUp != null)
                {
                    CollectPaths(journal._topicLookUp, Known);
                    _knownSeeded = true;
                }

                if (Known.Contains(path)) return;

                JournalEntry? entry = UnityEngine.Resources.Load<JournalEntry>(path);
                if (entry == null) entry = QuestSystem.FindJournalEntry(path);

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

        private static void CollectPaths(Il2CppSystem.Collections.Generic.Dictionary<AJournalTopic, Journal_RuntimeTopic> topics,
            ICollection<string> into)
        {
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
                    JournalEntry? entry = entries[index];
                    string? path = entry == null ? null : entry.GetResourcesPath();
                    if (string.IsNullOrEmpty(path)) continue;

                    into.Add(path);
                }
            }
        }

        /// <summary>Whether this journal already holds an entry with that resource path.</summary>
        private static bool IsInJournal(string path)
        {
            if (!TryGetJournal(out Journal? journal)) return false;

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
                    JournalEntry? entry = entries[index];
                    if (entry != null && entry.GetResourcesPath() == path) return true;
                }
            }

            return false;
        }

        /// <summary>Refuses an entry this journal already holds, whichever copy of the asset it arrives as.</summary>
        /// <returns>False to skip the add entirely.</returns>
        internal static bool AddEntryPrefix(Journal __instance, JournalEntry? entry, ref bool __result)
        {
            try
            {
                // A save load replays this world's own history through here.
                if (__instance != null && __instance.IsLoadingInProgress) return true;

                string? path = entry != null ? entry.GetResourcesPath() : null;
                if (string.IsNullOrEmpty(path)) return true;

                if (!IsInJournal(path)) return true;

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

        internal static void AddEntryPostfix(Journal __instance, JournalEntry? entry, bool __result)
        {
            try
            {
                if (!__result) return;
                if (!CoopSession.SharesWorld) return;
                if (RemoteApply<JournalScope>.InProgress) return;

                // A save load replays stored entries through AddEntry. Not news.
                if (__instance != null && __instance.IsLoadingInProgress) return;

                string? path = entry != null ? entry.GetResourcesPath() : null;
                if (string.IsNullOrEmpty(path)) return;

                // So the teammate's relayed copy of this entry is refused instead of listed twice.
                Known.Add(path);

                Shared++;
                ReportJournalEntry(path);

                MelonLogger.Msg($"[Journal] '{path}' was earned here and told to the session.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Journal] reporting an earned entry failed: " + e);
            }
        }

        internal static void ChangeTopicCategoryPostfix(Journal __instance, AJournalTopic? topic,
            JournalCategory? newCategory)
        {
            try
            {
                if (!CoopSession.SharesWorld) return;
                if (RemoteApply<JournalScope>.InProgress) return;
                if (__instance != null && __instance.IsLoadingInProgress) return;

                string? topicPath = topic != null ? topic.GetResourcesPath() : null;
                string? categoryPath = newCategory != null ? newCategory.GetResourcesPath() : null;
                if (string.IsNullOrEmpty(topicPath) || string.IsNullOrEmpty(categoryPath)) return;

                ReportJournalTopic(new JournalTopicMessage
                {
                    TopicPath = topicPath,
                    CategoryPath = categoryPath,
                });
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Journal] reporting a completion mark failed: " + e);
            }
        }

        private static bool TryGetJournal([NotNullWhen(true)] out Journal? journal)
        {
            journal = null;

            if (!PlayerMetaDataGameHandler.TryGet(out PlayerMetaDataGameHandler handler) || handler == null)
            {
                return false;
            }

            Journal? found = handler.GetJournal();
            if (found == null) return false;

            journal = found;
            return true;
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<JournalEntryMessage>(MessageIds.JournalEntry,
                (peer, m) => CoopSession.RelayAndQueue(peer, m, IncomingJournal, CoopSession.MaxPendingFromHost, "journal entries"));
            CoopSession.Register<JournalTopicMessage>(MessageIds.JournalTopic,
                (peer, m) => CoopSession.RelayAndQueue(peer, m, IncomingJournalTopics, CoopSession.MaxPendingFromHost, "completion marks"));
        }

        void ICoopSystem.EndSession()
        {
            IncomingJournal.Clear();
            IncomingJournalTopics.Clear();
        }

        /// <summary>Takes the next batch of shared journal entries.</summary>
        private static bool TryTakeJournal(out JournalEntryMessage message)
            => IncomingJournal.TryDequeue(out message);

        /// <summary>Reports a journal entry this player earned. The host adds it and passes it on.</summary>
        /// <param name="path">The entry's resource path.</param>
        private static void ReportJournalEntry(string path)
        {
            if (!CoopSession.SharesWorld) return;
            if (string.IsNullOrEmpty(path)) return;

            NetworkAccess.SendToAll(new JournalEntryMessage { Paths = [path] });
        }

        /// <summary>Takes the next completion mark that arrived.</summary>
        private static bool TryTakeJournalTopic(out JournalTopicMessage message)
            => IncomingJournalTopics.TryDequeue(out message);

        /// <summary>Reports a quest's journal topic changing category here. The host applies it and passes it on.</summary>
        private static void ReportJournalTopic(in JournalTopicMessage message)
            => CoopSession.SendIfLive(message);

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings) => Reset();
    }
}
