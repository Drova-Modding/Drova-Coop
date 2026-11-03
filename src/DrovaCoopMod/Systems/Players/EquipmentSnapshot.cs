namespace DrovaCoopMod.Systems.Players
{
    /// <summary>One player's appearance. Version counts arrivals, so a change check is an integer compare instead of a rebuild.</summary>
    /// <param name="ItemIds">Readable item ids.</param>
    /// <param name="ItemSets">The weapon set of each item.</param>
    /// <param name="ActiveSet">The weapon set in hand.</param>
    /// <param name="BodyType">The sender's build, as <c>ActorBodyType.BodyType</c>.</param>
    /// <param name="VoiceId">The sender's voice, as <c>VoiceID</c>.</param>
    /// <param name="Version">Appearance messages arrived for this player.</param>
    public readonly record struct EquipmentSnapshot(
        string[] ItemIds,
        byte[] ItemSets,
        byte ActiveSet,
        byte BodyType,
        byte VoiceId,
        int Version);
}
