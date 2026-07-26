namespace DrovaCoopMod.HeadlessPeer
{
    /// <summary>
    /// What the peer was asked to do. Hand-parsed rather than pulling in a command line library, because
    /// a diagnostic tool acquiring dependencies is how diagnostic tools stop being run.
    /// </summary>
    public sealed class Options
    {
        /// <summary>The options the running peer was started with.</summary>
        public static Options Current { get; private set; } = new();

        /// <summary>Relay hostname or address.</summary>
        public string Relay { get; private set; } = "127.0.0.1";

        /// <summary>Relay UDP port.</summary>
        public int Port { get; private set; } = 9060;

        /// <summary>Session code to join, or to claim when hosting. Null lets the relay pick one.</summary>
        public string? Code { get; private set; }

        /// <summary>Session password, which is also the encryption key. Null means an open session.</summary>
        public string? Password { get; private set; }

        /// <summary>The name announced in the handshake.</summary>
        public string Name { get; private set; } = "HeadlessPeer";

        /// <summary>Whether to host rather than join.</summary>
        public bool Host { get; private set; }

        /// <summary>Whether to stream player state once in a session.</summary>
        public bool Move { get; private set; }

        /// <summary>How long to stay up before leaving.</summary>
        public int Seconds { get; private set; } = 120;

        /// <summary>Center of the circle walked when <see cref="Move"/> is set.</summary>
        public float CenterX { get; private set; }

        /// <inheritdoc cref="CenterX"/>
        public float CenterY { get; private set; }

        /// <summary>Radius of that circle, in world units.</summary>
        public float Radius { get; private set; } = 3f;

        /// <summary>Whether usage was requested.</summary>
        public bool ShowHelp { get; private set; }

        /// <summary>
        /// Reads the arguments, falling back to defaults that work against a relay on this machine.
        /// </summary>
        public static Options Parse(string[] args)
        {
            Options options = new();

            for (int index = 0; index < args.Length; index++)
            {
                string argument = args[index];
                string? Next() => index + 1 < args.Length ? args[++index] : null;

                switch (argument)
                {
                    case "--relay": options.Relay = Next() ?? options.Relay; break;
                    case "--port": options.Port = int.TryParse(Next(), out int port) ? port : options.Port; break;
                    case "--code": options.Code = Next(); break;
                    case "--password": options.Password = Next(); break;
                    case "--name": options.Name = Next() ?? options.Name; break;
                    case "--seconds": options.Seconds = int.TryParse(Next(), out int seconds) ? seconds : options.Seconds; break;
                    case "--center-x": options.CenterX = float.TryParse(Next(), out float x) ? x : options.CenterX; break;
                    case "--center-y": options.CenterY = float.TryParse(Next(), out float y) ? y : options.CenterY; break;
                    case "--radius": options.Radius = float.TryParse(Next(), out float radius) ? radius : options.Radius; break;
                    case "--host": options.Host = true; break;
                    case "--move": options.Move = true; break;
                    case "--help" or "-h": options.ShowHelp = true; break;
                    default:
                        Console.Error.WriteLine("unknown argument: " + argument);
                        options.ShowHelp = true;
                        break;
                }
            }

            if (!options.ShowHelp && !options.Host && options.Code == null)
            {
                Console.Error.WriteLine("joining needs --code; pass --host to host instead.");
                options.ShowHelp = true;
            }

            Current = options;
            return options;
        }

        /// <summary>
        /// Prints how to use the tool.
        /// </summary>
        public static void PrintUsage()
        {
            Console.WriteLine("""
                A second coop player, without a second copy of Drova.

                  --host                 host a session instead of joining one
                  --code <code>          session code to join, or to claim when hosting
                  --relay <address>      relay address        (default 127.0.0.1)
                  --port <n>             relay port           (default 9060)
                  --password <password>  session password, also the encryption key
                  --name <name>          name announced in the handshake
                  --move                 stream player state in a slow circle
                  --center-x, --center-y, --radius   where that circle is
                  --seconds <n>          how long to stay up  (default 120)

                Join a game that is hosting:
                  DrovaCoopMod.HeadlessPeer --relay relay.example.com --code ABCD1234 --move

                Host, so a game can join this instead:
                  DrovaCoopMod.HeadlessPeer --relay relay.example.com --host --move
                """);
        }
    }
}
