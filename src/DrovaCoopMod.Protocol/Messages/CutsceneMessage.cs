using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A shared cutscene action starting. Each machine replays it against its own world and its own player.
    /// </summary>
    public struct CutsceneMessage : INetMessage
    {
        /// <summary>The longest asset name accepted.</summary>
        public const int MaxNameLength = 64;

        /// <summary>The cutscene data asset's name.</summary>
        public string Name;

        /// <summary>Which action of the cutscene to run.</summary>
        public int ActionId;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(WireStrings.Cut(Name, MaxNameLength));
            writer.Put(ActionId);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Name = WireStrings.Cut(reader.GetString(), MaxNameLength);
            ActionId = reader.GetInt();
        }
    }
}
