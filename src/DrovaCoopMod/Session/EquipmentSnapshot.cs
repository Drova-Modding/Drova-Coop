namespace DrovaCoopMod.Session
{
    /// <summary>
    /// One player's appearance, with a count of arrivals so "anything new" is an integer compare
    /// instead of an expensive rebuild.
    /// </summary>
    /// <param name="ItemIds">Readable item ids, as sent. Never null.</param>
    /// <param name="ItemSets">Which weapon set each item belongs to, one entry per item. Never null.</param>
    /// <param name="ActiveSet">Which of the sender's weapon sets is in hand.</param>
    /// <param name="BodyType">The sender's build, as <c>ActorBodyType.BodyType</c>.</param>
    /// <param name="VoiceId">The sender's voice, as <c>VoiceID</c>.</param>
    /// <param name="Version">How many appearance messages have arrived for this player.</param>
    public readonly record struct EquipmentSnapshot(
        string[] ItemIds,
        byte[] ItemSets,
        byte ActiveSet,
        byte BodyType,
        byte VoiceId,
        int Version);
}
