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
    /// Shows a teammate's conversation lines here as world speech bubbles over the speaker, read at
    /// <c>DS_DialogueUGUI.OnSubtitlesRequest</c>. An empty speaker guid names the sender's own body.
    /// </summary>
    internal static class SpokenLines
    {
        private const string LocaPath = "DrovaCoop/Said";

        /// <summary>Lines kept for the lookup; a bubble asks for its text when it starts, well within this many lines.</summary>
        private const int KeptLines = 32;

        private static readonly Dictionary<string, string> Texts = new(StringComparer.Ordinal);
        private static readonly Queue<string> Order = new();

        /// <summary>How long the same NPC saying the same line counts as one saying, so barks both machines trigger do not echo.</summary>
        private const float RepeatSeconds = 30f;

        // When each (speaker guid, line) was last seen here, said or shown.
        private static readonly Dictionary<string, float> Recent = new(StringComparer.Ordinal);

        private static WorldDialogueStarterModule? _starter;

        private static int _next;

        private static Actor? _bodySpeaking;
        private static string? _bodyLine;
        private static float _bodySince;

        // Voice clips for pending bubbles, by line text, taken by the bubble's subtitle request.
        private static readonly Dictionary<string, AudioClip> Voices = new(StringComparer.Ordinal);

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
            if (locaPath != LocaPath || locaKey == null || !Texts.TryGetValue(locaKey, out string? text)) return true;

            __result = text;
            return false;
        }

        private static void SubtitlesPrefix(DS_DialogueUGUI __instance, SubtitlesRequestInfo info)
        {
            if (!CoopSession.IsLive || info == null) return;

            try
            {
                if (info.statement?.text is string said && Voices.Remove(said, out AudioClip? voice))
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

                // Ambient lines (barks, shouts) are started per machine, so they are sent too but deduplicated.
                DialogueTree? tree = PlayerTree();
                bool ambient = tree == null || __instance.DLGTree == null || __instance.DLGTree.Pointer != tree.Pointer;

                string? text = info.statement?.text;
                Actor? speaker = info.actor?.TryCast<DS_DialogueActor>()?.Actor;
                if (string.IsNullOrEmpty(text) || speaker == null || RemotePlayers.IsPuppet(speaker)) return;

                // The player character's own line is sent with no speaker, which names the sender's body over there.
                if (PlayerAccess.TryGetPlayer(out Actor self) && self.Pointer == speaker.Pointer)
                {
                    if (!ambient) SayOwn(text, info.statement?.audio);
                    return;
                }

                string guid = speaker.GetGuidComponent()?._guidString ?? string.Empty;
                if (guid.Length == 0) return;

                // Also keeps a bubble shown from a teammate from echoing back.
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

        /// <summary>Fires for plain and hub answers alike; hub answers never reach the request's own SelectOption.</summary>
        private static void FinalizePrefix(DS_DialogueUGUI __instance, MultipleChoiceRequestInfo info, int index) => SayChoice(__instance, info, index);

        /// <summary>Sends the picked answer as the player's own line, since an unvoiced answer is never shown as a line.</summary>
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

        private static void SayOwn(string? text, AudioClip? voice)
        {
            // Stripped so the picked answer and the game voicing it count as one line.
            text = Spoken(text);
            if (string.IsNullOrEmpty(text) || !IsNew(string.Empty, text)) return;

            Sent++;
            CoopSession.ReportDialogueLine(new DialogueLineMessage
            {
                Player = CoopSession.LocalPlayerId, SpeakerGuid = string.Empty, Text = text, Ambient = false,
                Voice = voice?.name ?? string.Empty, VoiceActor = PlayerVoice,
            });
        }

        /// <summary>The player's root dialogue tree, which its window holds even while a sub-dialogue runs.</summary>
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

                // Both players near an NPC trigger its barks on both machines.
                if (!IsNew(message.SpeakerGuid, message.Text) && message.Ambient) return;
                if (message.Ambient && speaker.DialogueModule.IsInDialogue()) return;

                string key = (++_next).ToString();
                Texts[key] = message.Text;
                Order.Enqueue(key);
                while (Order.Count > KeptLines) Texts.Remove(Order.Dequeue());

                // A busy speaker refuses a new bark.
                if (DialogueSystemGameHandler.TryGet(out DialogueSystemGameHandler handler) && handler != null) handler.StopWorldDialoguesForActor(speaker);

                // The game's own pooled bark template: a tree built here shows no bubble and breaks the graph pool.
                _starter ??= new WorldDialogueStarterModule();
                WorldDialogueArgs settings = _starter._worldDialogueSettings;
                settings.SetNewGraphLocaPath(LocaPath);
                settings.Args.Clear();
                settings.Args.Add(new WorldDialogueArg { _path = LocaPath, _key = key });
                if (message.Voice.Length > 0 && Drova_Modding_API.Systems.Audio.AudioManager.FindDialogueClip(message.VoiceActor, message.Voice) is AudioClip clip)
                {
                    Voices[message.Text] = clip;
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
            bool fresh = !Recent.TryGetValue(key, out float at) || now - at >= RepeatSeconds;
            Recent[key] = now;

            if (Recent.Count > 256)
            {
                foreach (string stale in Recent.Where(pair => now - pair.Value >= RepeatSeconds).Select(pair => pair.Key).ToList()) Recent.Remove(stale);
            }

            return fresh;
        }

        /// <summary>Forgets the bubbles. Called when a session ends.</summary>
        internal static void Reset()
        {
            Recent.Clear();
            _bodySpeaking = null;
            Voices.Clear();
            Texts.Clear();
            Order.Clear();
            Sent = 0;
            Shown = 0;
            BodyShown = 0;
            Voiced = 0;
        }
    }
}
