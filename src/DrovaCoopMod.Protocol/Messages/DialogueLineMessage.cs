using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// An NPC line from a player's conversation or an ambient dialogue, shown as a bubble (and voice) on every other machine.
    /// The host stamps <see cref="Player"/> from the connection.
    /// </summary>
    public struct DialogueLineMessage : INetMessage
    {
        /// <summary>Longest line carried, in characters. Fits a relayed packet at three UTF-8 bytes each.</summary>
        public const int MaxTextLength = 320;

        /// <summary>Longest voice clip name carried (a Modding API bundle key).</summary>
        public const int MaxVoiceLength = 100;

        /// <summary>Longest voice actor name carried.</summary>
        public const int MaxVoiceActorLength = 40;

        /// <summary>Whose conversation it is.</summary>
        public PlayerId Player;

        /// <summary>The NPC who says it, by entity guid. Empty for the sender's own character.</summary>
        public string SpeakerGuid;

        /// <summary>The line as the speaker's machine shows it.</summary>
        public string Text;

        /// <summary>An ambient world dialogue (a bark, a fight shout) rather than the player's conversation.</summary>
        public bool Ambient;

        /// <summary>The voice clip name, or empty when the line has none.</summary>
        public string Voice;

        /// <summary>The speaker's dialogue actor name, whose voice bundle holds the clip.</summary>
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

        // Only ASCII is budgeted for asset names, anything else means no voice.
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
