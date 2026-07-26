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

        /// <summary>
        /// Copy the other player's intent instead of inventing motion, offset a few units to the side.
        ///
        /// This is the honest way to exercise replication. Fabricated motion has to guess the game's run
        /// speed, and a guess that is wrong shows up at the far end as a body that permanently trails its
        /// own reported position - which looks like a replication fault and is not one. Mirroring makes
        /// the speeds agree by construction, because they are the same player's.
        /// </summary>
        public bool Mirror { get; private set; }

        /// <summary>How far to one side a mirrored body walks.</summary>
        public float MirrorOffset { get; private set; } = 3f;

        /// <summary>How long to stay up before leaving.</summary>
        public int Seconds { get; private set; } = 120;

        /// <summary>Center of the circle walked when <see cref="Move"/> is set.</summary>
        public float CenterX { get; private set; }

        /// <inheritdoc cref="CenterX"/>
        public float CenterY { get; private set; }

        /// <summary>
        /// How fast this peer claims to move, in world units per second.
        ///
        /// It has to be close to the game's own run speed. The far end drives the body through the game's
        /// movement module, so the body moves at the game's speed no matter what this says; a mismatch
        /// shows up as the body constantly drifting from the position this peer reports, and being
        /// corrected for it.
        /// </summary>
        public float Speed { get; private set; } = 6f;

        /// <summary>How fast the walked direction turns, in radians per second.</summary>
        public float TurnRate { get; private set; } = 1f;

        /// <summary>How long to keep moving before standing still.</summary>
        public float WalkSeconds { get; private set; } = 4f;

        /// <summary>
        /// How long to stand still before moving again. Idling matters: the far end takes its position
        /// correction while a body is stationary, because doing it mid-stride is visible.
        /// </summary>
        public float StandSeconds { get; private set; } = 1.5f;

        /// <summary>Radius of the circle, kept for placing the starting point.</summary>
        public float Radius { get; private set; } = 3f;

        /// <summary>
        /// Whether a center was given. When it was not, the peer adopts the first position it hears from
        /// somebody else instead of orbiting the world origin - which is nowhere near where anyone plays,
        /// and would put this peer's body far enough away to be invisible.
        /// </summary>
        public bool HasCenter { get; private set; }

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
                    case "--center-x": options.CenterX = float.TryParse(Next(), out float x) ? x : options.CenterX; options.HasCenter = true; break;
                    case "--center-y": options.CenterY = float.TryParse(Next(), out float y) ? y : options.CenterY; options.HasCenter = true; break;
                    case "--radius": options.Radius = float.TryParse(Next(), out float radius) ? radius : options.Radius; break;
                    case "--speed": options.Speed = float.TryParse(Next(), out float speed) ? speed : options.Speed; break;
                    case "--turn-rate": options.TurnRate = float.TryParse(Next(), out float turn) ? turn : options.TurnRate; break;
                    case "--walk-seconds": options.WalkSeconds = float.TryParse(Next(), out float walk) ? walk : options.WalkSeconds; break;
                    case "--stand-seconds": options.StandSeconds = float.TryParse(Next(), out float stand) ? stand : options.StandSeconds; break;
                    case "--host": options.Host = true; break;
                    case "--move": options.Move = true; break;
                    case "--mirror": options.Mirror = true; break;
                    case "--mirror-offset": options.MirrorOffset = float.TryParse(Next(), out float offset) ? offset : options.MirrorOffset; break;
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
                  --move                 walk in a circle and stream the intent for it
                  --speed <n>            world units per second   (default 6)
                  --turn-rate <n>        radians per second       (default 1)
                  --walk-seconds <n>     how long to move         (default 4)
                  --stand-seconds <n>    how long to idle between (default 1.5)
                  --center-x, --center-y, --radius   where to start walking
                  --seconds <n>          how long to stay up  (default 120)

                Join a game that is hosting:
                  DrovaCoopMod.HeadlessPeer --relay relay.example.com --code ABCD1234 --move

                Host, so a game can join this instead:
                  DrovaCoopMod.HeadlessPeer --relay relay.example.com --host --move
                """);
        }
    }
}
