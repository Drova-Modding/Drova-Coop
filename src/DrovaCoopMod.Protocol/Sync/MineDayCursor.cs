namespace DrovaCoopMod.Sync
{
    /// <summary>
    /// The mine bed's day index: one past the first begun day, as the bed's <c>Awake</c> computes it.
    /// </summary>
    public static class MineDayCursor
    {
        /// <summary>Never moves backward, since a started morning runs a slot ahead of its flag.</summary>
        /// <param name="begun">For each day in order, whether the world says it has already begun.</param>
        /// <param name="current">Where the cursor is now.</param>
        public static int Resolve(bool[]? begun, int current)
        {
            return Math.Max(current, begun == null ? 0 : Array.IndexOf(begun, true) + 1);
        }
    }
}
