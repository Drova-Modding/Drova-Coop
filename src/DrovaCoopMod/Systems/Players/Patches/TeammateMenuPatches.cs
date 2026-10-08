namespace DrovaCoopMod.Systems.Players.Patches
{
    /// <summary>The Harmony entry points of <see cref="TeammateMenu"/>. Each forwards to the system.</summary>
    internal static class TeammateMenuPatches
    {
        internal static bool LocalizePrefix(string locaPath, string locaKey, ref string __result)
            => TeammateMenu.LocalizePrefix(locaPath, locaKey, ref __result);
    }
}
