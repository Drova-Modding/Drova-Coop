namespace DrovaCoopMod.Systems.Players
{
    /// <summary>The last cast one player announced. Version counts arrivals, so the same spell cast twice reads as two casts.</summary>
    /// <param name="ItemId">Readable id of the ability item.</param>
    /// <param name="Sequence">The sender's cast counter.</param>
    /// <param name="Released">Whether the cast has been let go.</param>
    /// <param name="Version">Cast messages arrived for this player.</param>
    public readonly record struct ItemUseSnapshot(string ItemId, byte Sequence, bool Released, int Version);
}
