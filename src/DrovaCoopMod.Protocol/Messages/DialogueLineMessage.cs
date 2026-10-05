using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A line an NPC speaks in a player's conversation or in an ambient world dialogue, so every other machine shows it as
    /// a speech bubble over that NPC's copy there. A conversation runs only on the machine of the player in it,
    /// and the teammate standing beside them used to watch two people stand still. Owner to host, host to everyone else;
    /// the host stamps <see cref="Player"/> from the connection. A voiced line names its clip, so the bubble is heard too.
    /// </summary>
    public struct DialogueLineMessage : INetMessage
    {
        /// <summary>
        /// The longest line carried, in characters. The game's longest lines are a few hundred; at three UTF-8 bytes a
        /// character this still fits what a relay forwards, beside the voice's names.
        /// </summary>
        public const int MaxTextLength = 320;

        /// <summary>The longest voice clip name carried. Clip names are the Modding API's bundle keys: tree, line key, path, actor.</summary>
        public const int MaxVoiceLength = 100;

        /// <summary>The longest name of the character whose voice bundle holds the clip.</summary>
        public const int MaxVoiceActorLength = 40;

        /// <summary>Whose conversation it is.</summary>
        public PlayerId Player;

        /// <summary>The NPC who says it, by entity guid; empty for the sender's own character.</summary>
        public string SpeakerGuid;

        /// <summary>The line as the speaker's machine shows it.</summary>
        public string Text;

        /// <summary>An ambient world dialogue (a bark, a fight shout) rather than the player's conversation.</summary>
        public bool Ambient;

        /// <summary>The voice clip the speaker's machine plays for the line, by name; empty when it has none.</summary>
        public string Voice;

        /// <summary>Whose voice bundle the clip came from (the speaker's dialogue actor name).</summary>
        public string VoiceActor;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Player.Value);
            writer.Put(WireStrings.Cut(SpeakerGuid, WireStrings.MaxGuidLength));
            writer.Put(WireStrings.Cut(Text, MaxTextLength));
            writer.Put(Ambient);
            writer.Put(Names(Voice, MaxVoiceLength));
            writer.Put(Names(VoiceActor, MaxVoiceActorLength));
        }

        // Asset names are ASCII, and only ASCII is budgeted for them: anything else is no voice rather than a line too big
        // to cross.
        private static string Names(string? value, int limit)
        {
            value = WireStrings.Cut(value, limit);
            foreach (char c in value) if (c > 127) return string.Empty;
            return value;
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Player = new PlayerId(reader.GetByte());
            SpeakerGuid = WireStrings.Cut(reader.GetString(), WireStrings.MaxGuidLength);
            Text = WireStrings.Cut(reader.GetString(), MaxTextLength);
            Ambient = reader.GetBool();
            Voice = Names(reader.GetString(), MaxVoiceLength);
            VoiceActor = Names(reader.GetString(), MaxVoiceActorLength);
        }
    }
}
