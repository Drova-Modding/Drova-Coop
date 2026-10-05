using Drova_Modding_API;
using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.DialogueNew;
using Il2CppNodeCanvas.DialogueTrees;
using Il2CppNodeCanvas.DialogueTrees.UI;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// A teammate's conversation, seen from beside them: each line they hear or say shows here as the game's own world
    /// speech bubble over the NPC speaking, its copy here, and the player character's own lines over that teammate's body.
    /// A conversation runs only on the machine of the player in it, so the others watched two people stand silent.
    ///
    /// Read where every shown line goes through, <c>DS_DialogueUGUI.OnSubtitlesRequest</c> (a private method subscribed as
    /// a delegate, so never inlined), and only for the player's own conversation - which leaves out ambient barks and the
    /// bubbles shown here, so nothing echoes. Shown the way the game shows a bark: a world dialogue starter on its own template. The text travels as shown (in the speaker's
    /// language) and is served to that tree through the localization lookup, as <see cref="TeammateMenu"/>'s lines are.
    /// What the player character says - the answer picked, and any line of its own - crosses with an empty speaker guid,
    /// which names the sender's body: the bark started on that body opens over it (measured). A voiced line names its clip
    /// (the Modding API's bundles), and the bubble here plays it: handed to the bubble's statement before the API's own
    /// subtitle postfix, which plays a statement's clip at its speaker.
    /// </summary>
    internal static class SpokenLines
    {
        private const string LocaPath = "DrovaCoop/Said";

        /// <summary>Lines kept for the lookup; a bubble asks for its text when it starts, well within this many lines.</summary>
        private const int KeptLines = 32;

        private static readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);
        private static readonly Queue<string> _order = new();

        /// <summary>
        /// How long the same NPC saying the same line counts as one saying: a bark both machines trigger on their own, or a
        /// line received back that this machine showed, is not shown or sent again.
        /// </summary>
        private const float RepeatSeconds = 30f;

        // When each (speaker guid, line) was last seen here, said or shown.
        private static readonly Dictionary<string, float> _recent = new(StringComparer.Ordinal);

        private static WorldDialogueStarterModule? _starter;

        private static int _next;

        // A teammate's own line on its way to its bubble, to count it once it opens over their body (bodylines).
        private static Actor? _bodySpeaking;
        private static string? _bodyLine;
        private static float _bodySince;

        // Voice clips for bubbles on their way, by the line they belong to; taken by the bubble's subtitle request.
        private static readonly Dictionary<string, AudioClip> _voices = new(StringComparer.Ordinal);

        /// <summary>How long a teammate's own line waits for its bubble: the bark first waits for its participants.</summary>
        private const float BodyLineSeconds = 5f;

        /// <summary>Lines sent from this machine's conversations. For tests.</summary>
        internal static int Sent { get; private set; }

        /// <summary>Teammates' lines shown here as bubbles. For tests.</summary>
        internal static int Shown { get; private set; }

        /// <summary>Teammates' lines that arrived with a voice clip found here. For tests.</summary>
        internal static int Voiced { get; private set; }

        /// <summary>Teammates' own lines shown over their body. For tests.</summary>
        internal static int BodyShown { get; private set; }

        /// <summary>Applies the patches. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPrefix(harmony, typeof(DS_DialogueUGUI), "OnSubtitlesRequest", typeof(SpokenLines), nameof(SubtitlesPrefix));
            // By its parameters: a lookup by name alone lands on Il2CppSystem.Object.Finalize.
            Hooking.TryPrefix(harmony, HarmonyLib.AccessTools.Method(typeof(DS_DialogueUGUI), "Finalize", [typeof(MultipleChoiceRequestInfo), typeof(int)]),
                typeof(SpokenLines), nameof(FinalizePrefix), "DS_DialogueUGUI.Finalize");
            Hooking.TryPrefix(harmony, HarmonyLib.AccessTools.Method(typeof(Il2CppCustomFramework.Localization.LocalizationDB), "Localize",
                [typeof(string), typeof(string), typeof(Il2CppSystem.Object), typeof(bool)]), typeof(SpokenLines), nameof(LocalizePrefix), "LocalizationDB.Localize");
        }

        private static bool LocalizePrefix(string locaPath, string locaKey, ref string __result)
        {
            if (locaPath != LocaPath || locaKey == null || !_texts.TryGetValue(locaKey, out string? text)) return true;

            __result = text;
            return false;
        }

        private static void SubtitlesPrefix(DS_DialogueUGUI __instance, SubtitlesRequestInfo info)
        {
            if (!CoopSession.IsLive || info == null) return;

            try
            {
                if (info.statement?.text is string said && _voices.Remove(said, out AudioClip? voice))
                {
                    DS_Statement? statement = info.statement.TryCast<DS_Statement>();
                    if (statement != null) statement.audio = voice;
                }

                if (_bodySpeaking != null && Time.unscaledTime - _bodySince < BodyLineSeconds && info.statement?.text == _bodyLine
                    && info.actor?.TryCast<DS_DialogueActor>()?.Actor?.Pointer == _bodySpeaking.Pointer)
                {
                    _bodySpeaking = null;
                    BodyShown++;
                }

                // The player's own conversation, or an ambient world dialogue: barks, fight shouts, NPCs talking among
                // themselves. Each machine starts those on its own, so one player heard them and the other did not.
                DialogueTree? tree = PlayerTree();
                bool ambient = tree == null || __instance.DLGTree == null || __instance.DLGTree.Pointer != tree.Pointer;

                string? text = info.statement?.text;
                Actor? speaker = info.actor?.TryCast<DS_DialogueActor>()?.Actor;
                if (string.IsNullOrEmpty(text) || speaker == null || RemotePlayers.IsPuppet(speaker)) return;

                // The player character's own line, in this player's conversation: sent with no speaker, which names the
                // sender's body over there.
                if (PlayerAccess.TryGetPlayer(out Actor self) && self.Pointer == speaker.Pointer)
                {
                    if (!ambient) SayOwn(text, info.statement?.audio);
                    return;
                }

                string guid = speaker.GetGuidComponent()?._guidString ?? string.Empty;
                if (guid.Length == 0) return;

                // Already said here or shown from a teammate - which is also how a shown bubble is kept from echoing back.
                if (ambient && !IsNew(guid, text)) return;

                Sent++;
                CoopSession.ReportDialogueLine(new DialogueLineMessage
                {
                    Player = CoopSession.LocalPlayerId, SpeakerGuid = guid, Text = text, Ambient = ambient,
                    Voice = info.statement?.audio?.name ?? string.Empty, VoiceActor = info.actor?.name ?? string.Empty,
                });
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Lines] reading a dialogue line failed: " + e);
            }
        }

        /// <summary>
        /// Where the dialogue window settles the answer the player picked, a plain one or a hub's: measured firing for both
        /// (Olaf's "work" is a hub answer, which never reaches the request's own SelectOption).
        /// </summary>
        private static void FinalizePrefix(DS_DialogueUGUI __instance, MultipleChoiceRequestInfo info, int index) => SayChoice(__instance, info, index);

        /// <summary>
        /// The answer this player picked, which is what the player character says: sent as the player's own line. Without a
        /// voice clip the game never shows it as a line of its own, so it is read where the choice is made.
        /// </summary>
        private static void SayChoice(DS_DialogueUGUI window, MultipleChoiceRequestInfo info, int index)
        {
            if (!CoopSession.IsLive || info?.options == null) return;

            try
            {
                foreach (Il2CppSystem.Collections.Generic.KeyValuePair<IStatement, int> option in info.options)
                {
                    if (option.Value != index) continue;
                    SayOwn(option.Key?.text, option.Key?.audio ?? Drova_Modding_API.Systems.Audio.AudioManager.AnswerClip(window, index));
                    return;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Lines] reading a chosen answer failed: " + e);
            }
        }

        // The player character's voice bundle (the API preloads it under this name).
        private const string PlayerVoice = "player";

        /// <summary>An answer as said: without the window's marker for what it does, "(end conversation)".</summary>
        private static string? Spoken(string? text)
        {
            if (text == null) return null;
            text = text.TrimEnd();
            int open = text.LastIndexOf(" (", StringComparison.Ordinal);
            return open > 0 && text.EndsWith(")", StringComparison.Ordinal) ? text[..open] : text;
        }

        /// <summary>A line the player character says, sent once for the sender's body.</summary>
        private static void SayOwn(string? text, AudioClip? voice)
        {
            // Without the marker either way: the answer picked and the game saying it (a voiced answer) are one line.
            text = Spoken(text);
            if (string.IsNullOrEmpty(text) || !IsNew(string.Empty, text)) return;

            Sent++;
            CoopSession.ReportDialogueLine(new DialogueLineMessage
            {
                Player = CoopSession.LocalPlayerId, SpeakerGuid = string.Empty, Text = text, Ambient = false,
                Voice = voice?.name ?? string.Empty, VoiceActor = PlayerVoice,
            });
        }

        /// <summary>
        /// The player's own conversation: the root tree, which is the one its window holds - also while a sub-dialogue
        /// inside it runs (measured: Olaf's FeedingTheSheep lines came through the window of DT_PreApo_1_NPC_Olaf).
        /// </summary>
        private static DialogueTree? PlayerTree()
        {
            if (!DialogueSystemGameHandler.TryGet(out DialogueSystemGameHandler handler) || handler?.ActivePlayerDialogue == null
                || !handler.ActivePlayerDialogue.isRunning) return null;

            return handler.ActivePlayerDialogue.graph?.TryCast<DialogueTree>();
        }

        /// <summary>Shows a teammate's line over its speaker here. Called where the message arrives.</summary>
        internal static void Show(in DialogueLineMessage message)
        {
            if (message.Player.Value == CoopSession.LocalPlayerId.Value || string.IsNullOrEmpty(message.Text)) return;

            try
            {
                Actor? speaker = Speaker(message);
                if (speaker == null || speaker.IsDestroyed || !speaker.gameObject.activeInHierarchy || speaker.IsDead()) return;

                // This player is talking to them: the bubble would start over the conversation.
                if (speaker.DialogueModule == null || speaker.DialogueModule.IsInDialogue(checkWorldDialogue: false)) return;

                // An ambient line gives way to a bark of the speaker's own here, and to a line already said: both players near
                // an NPC trigger its barks on both machines.
                if (!IsNew(message.SpeakerGuid, message.Text) && message.Ambient) return;
                if (message.Ambient && speaker.DialogueModule.IsInDialogue()) return;

                string key = (++_next).ToString();
                _texts[key] = message.Text;
                _order.Enqueue(key);
                while (_order.Count > KeptLines) _texts.Remove(_order.Dequeue());

                // The line before it, or a bark of the speaker's own, gives way: a busy speaker refuses a new one.
                if (DialogueSystemGameHandler.TryGet(out DialogueSystemGameHandler handler) && handler != null) handler.StopWorldDialoguesForActor(speaker);

                // The game's own bark, on its own pooled template: a tree built here ran, but showed no bubble and broke the
                // pool it was handed back to ("Could not find graph pool").
                _starter ??= new WorldDialogueStarterModule();
                WorldDialogueArgs settings = _starter._worldDialogueSettings;
                settings.SetNewGraphLocaPath(LocaPath);
                settings.Args.Clear();
                settings.Args.Add(new WorldDialogueArg { _path = LocaPath, _key = key });
                if (message.Voice.Length > 0 && Drova_Modding_API.Systems.Audio.AudioManager.FindDialogueClip(message.VoiceActor, message.Voice) is AudioClip clip)
                {
                    _voices[message.Text] = clip;
                    Voiced++;
                }

                bool body = message.SpeakerGuid.Length == 0;
                if (body)
                {
                    _bodySpeaking = speaker;
                    _bodyLine = message.Text;
                    _bodySince = Time.unscaledTime;
                }

                _starter.StartDialogue(speaker);

                if (speaker.DialogueModule.IsInDialogue()) Shown++;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Lines] showing a teammate's dialogue line failed: " + e);
            }
        }

        private static Actor? Speaker(in DialogueLineMessage message)
        {
            if (message.SpeakerGuid.Length == 0) return RemotePlayers.Bodies.TryGetValue(message.Player, out RemotePlayer? player) ? player?.Body : null;

            return EntityGameHandler.TryGet(out EntityGameHandler entities) ? entities?.GetActorFromGuid(message.SpeakerGuid) : null;
        }

        /// <summary>Whether this speaker's line is new here, remembering it either way.</summary>
        private static bool IsNew(string guid, string text)
        {
            float now = Time.unscaledTime;
            string key = guid + "|" + text;
            bool fresh = !_recent.TryGetValue(key, out float at) || now - at >= RepeatSeconds;
            _recent[key] = now;

            if (_recent.Count > 256)
            {
                foreach (string stale in _recent.Where(pair => now - pair.Value >= RepeatSeconds).Select(pair => pair.Key).ToList()) _recent.Remove(stale);
            }

            return fresh;
        }

        /// <summary>Forgets the bubbles. Called when a session ends.</summary>
        internal static void Reset()
        {
            _recent.Clear();
            _bodySpeaking = null;
            _voices.Clear();
            _texts.Clear();
            _order.Clear();
            Sent = 0;
            Shown = 0;
            BodyShown = 0;
            Voiced = 0;
        }
    }
}
