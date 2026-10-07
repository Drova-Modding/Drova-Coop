namespace DrovaCoopMod.Session
{
    /// <summary>
    /// The last cast one player announced. The version counts arrivals, so casting the same spell twice
    /// reads as two casts.
    /// </summary>
    /// <param name="ItemId">Readable id of the ability item, as sent. Never null.</param>
    /// <param name="Sequence">Which cast this is, as the sender counted it.</param>
    /// <param name="Released">Whether the cast has been let go.</param>
    /// <param name="Version">How many cast messages have arrived for this player.</param>
    public readonly record struct ItemUseSnapshot(string ItemId, byte Sequence, bool Released, int Version);
}
