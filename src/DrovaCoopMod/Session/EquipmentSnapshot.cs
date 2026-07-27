namespace DrovaCoopMod.Session
{
    /// <summary>
    /// What one player looks like - their build, their voice and what they are wearing - and how many
    /// times that has been heard about.
    ///
    /// The count is the working part. Putting appearance on a body means taking every slot apart and
    /// building it back up, which is far too much to do every frame and impossible to skip by comparing
    /// item ids - that comparison costs more than the thing it is avoiding once the lists are the same
    /// length, which they nearly always are. Counting arrivals instead makes "is there anything new"
    /// an integer comparison.
    /// </summary>
    /// <param name="ItemIds">Readable item ids, as sent. Never null.</param>
    /// <param name="BodyType">The sender's build, as <c>ActorBodyType.BodyType</c>.</param>
    /// <param name="VoiceId">The sender's voice, as <c>VoiceID</c>.</param>
    /// <param name="Version">How many appearance messages have arrived for this player.</param>
    public readonly record struct EquipmentSnapshot(string[] ItemIds, byte BodyType, byte VoiceId, int Version);
}
