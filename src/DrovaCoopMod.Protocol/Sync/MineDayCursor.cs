namespace DrovaCoopMod.Sync
{
    /// <summary>
    /// Where the mine's bed belongs in its list of days, given which of those days the world says have
    /// already begun.
    ///
    /// Extracted from the reseat that uses it so the one part of that fix which is not about Unity can be
    /// tested. The rule is the game's own, read out of the bed's <c>Awake</c>: the cursor sits one past
    /// the first day whose "already begun" test passes. What is added here is that it never moves
    /// backwards, and that is not tidiness - between a player starting a morning and the scene writing
    /// that day's flag, their own world is legitimately a slot ahead of what the variables say, and
    /// rewinding there would let the same morning be started twice.
    /// </summary>
    public static class MineDayCursor
    {
        /// <summary>
        /// Where the cursor belongs.
        /// </summary>
        /// <param name="begun">For each day in order, whether the world says it has already begun.</param>
        /// <param name="current">Where the cursor is now.</param>
        public static int Resolve(bool[]? begun, int current)
        {
            int want = 0;

            for (int index = 0; index < (begun?.Length ?? 0); index++)
            {
                if (!begun![index]) continue;

                want = index + 1;
                break;
            }

            return want > current ? want : current;
        }
    }
}
