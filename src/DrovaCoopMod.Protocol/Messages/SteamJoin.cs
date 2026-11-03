namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// The join string a Steam host shows its friends. Steam hands it back on "Join Game", or appends it to the command
    /// line when it has to start the game first, so it must survive both as one string and split into arguments.
    /// </summary>
    public static class SteamJoin
    {
        /// <summary>Marks the join string as this mod's among every other game's command-line arguments.</summary>
        public const string Switch = "+drova_coop_join";

        /// <summary>The join string for a host's Steam id.</summary>
        public static string Format(ulong hostSteamId) => Switch + " " + hostSteamId;

        /// <summary>Reads the host's Steam id out of a join string.</summary>
        public static bool TryParse(string? joinString, out ulong hostSteamId)
        {
            hostSteamId = 0;
            if (string.IsNullOrWhiteSpace(joinString)) return false;

            return TryParse(joinString.Split(' ', StringSplitOptions.RemoveEmptyEntries), out hostSteamId);
        }

        /// <summary>Finds a join string among command-line arguments.</summary>
        public static bool TryParse(IReadOnlyList<string> arguments, out ulong hostSteamId)
        {
            hostSteamId = 0;

            for (int index = 0; index + 1 < arguments.Count; index++)
            {
                if (arguments[index] != Switch) continue;

                return ulong.TryParse(arguments[index + 1], out hostSteamId) && hostSteamId != 0;
            }

            return false;
        }
    }
}
