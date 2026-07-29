namespace DrovaCoopMod.Session
{
    /// <summary>
    /// The last cast one player announced, and how many times anything has been heard from them about
    /// casting.
    ///
    /// The version is what makes this a record of an event rather than of a value. A cast is not a
    /// quantity the body should keep matching - it happened once - so the body needs to know that
    /// something new arrived rather than what the current state is, and comparing an ability id would
    /// answer the wrong question: casting the same spell twice in a row is two casts, not one.
    /// </summary>
    /// <param name="ItemId">Readable id of the ability item, as sent. Never null.</param>
    /// <param name="Sequence">Which cast this is, as the sender counted it.</param>
    /// <param name="Released">Whether the cast has been let go.</param>
    /// <param name="Version">How many cast messages have arrived for this player.</param>
    public readonly record struct ItemUseSnapshot(string ItemId, byte Sequence, bool Released, int Version);
}
