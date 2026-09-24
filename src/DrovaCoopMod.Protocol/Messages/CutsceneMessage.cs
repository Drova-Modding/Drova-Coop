using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A shared cutscene starting, so a story transition that moves one player moves all of them.
    ///
    /// Only cutscenes a mod has declared shared cross at all. Most cutscene actions are dialogue
    /// staging for one player's conversation, and replaying those would hijack every other screen.
    /// What crosses is the cutscene data asset's name and the action to run, and each machine replays
    /// it against its own world, so "the player" in the actions is that machine's own player.
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
