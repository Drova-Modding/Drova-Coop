using DrovaCoopMod.SaveDiff;

// Compares two Drova saves and reports what the second holds that the first did not.
//
// The question it answers is the one the mod cannot answer by playing: did having a remote player in the
// world put anything into the save. Take a baseline with no session, take a candidate with a body standing
// in the world, and run this. A clean result is no new names and no changed counts - the two saves describe
// the same set of things, differing only in the numbers inside them.
//
// Read the output as a comparison against a control, never on its own. Two saves taken minutes apart
// legitimately differ: a chunk streamed in, an NPC moved, a container was opened. So run it once on two
// no-session saves first, see what a session-free difference looks like, and then judge the real run
// against that rather than against zero.

if (args.Length is < 2 or > 2 || args[0] is "-h" or "--help")
{
    Console.WriteLine("usage: DrovaCoopMod.SaveDiff <baseline> <candidate>");
    Console.WriteLine();
    Console.WriteLine("  Each argument is a data.sav, or the Savegame_NN folder holding one.");
    Console.WriteLine("  Saves live under %USERPROFILE%\\AppData\\LocalLow\\Just2D\\Drova.");
    return 2;
}

string? baselinePath = Resolve(args[0]);
string? candidatePath = Resolve(args[1]);

if (baselinePath == null || candidatePath == null)
{
    Console.Error.WriteLine($"No data.sav at '{(baselinePath == null ? args[0] : args[1])}'.");
    return 2;
}

byte[] baseline = File.ReadAllBytes(baselinePath);
byte[] candidate = File.ReadAllBytes(candidatePath);

Console.WriteLine($"baseline  {baselinePath}  {baseline.Length:N0} bytes");
Console.WriteLine($"candidate {candidatePath}  {candidate.Length:N0} bytes");
Console.WriteLine($"size delta {candidate.Length - baseline.Length:+#,0;-#,0;0} bytes");
Console.WriteLine();

if (baseline.AsSpan().SequenceEqual(candidate))
{
    Console.WriteLine("The two files are byte-identical.");
    return 0;
}

Dictionary<string, int> before = SaveStrings.Count(baseline);
Dictionary<string, int> after = SaveStrings.Count(candidate);

// Sorted rather than in the order found, so two runs of this tool can themselves be compared.
List<string> appeared = after.Keys.Where(name => !before.ContainsKey(name)).OrderBy(name => name, StringComparer.Ordinal).ToList();
List<string> vanished = before.Keys.Where(name => !after.ContainsKey(name)).OrderBy(name => name, StringComparer.Ordinal).ToList();

List<(string Name, int Before, int After)> changed = before
    .Where(entry => after.TryGetValue(entry.Key, out int now) && now != entry.Value)
    .Select(entry => (entry.Key, entry.Value, after[entry.Key]))
    // Largest change first: one more of something is ordinary, two hundred more is the bug.
    .OrderByDescending(entry => Math.Abs(entry.Item3 - entry.Value))
    .ToList();

Report("Only in the candidate - saved now, not saved before", appeared);
Report("Only in the baseline - saved before, not saved now", vanished);

if (changed.Count > 0)
{
    Console.WriteLine($"Counts that changed ({changed.Count}):");
    foreach ((string name, int wasCount, int nowCount) in changed.Take(40))
    {
        Console.WriteLine($"  {wasCount,6} -> {nowCount,-6} {(nowCount - wasCount):+#,0;-#,0}  {name}");
    }

    if (changed.Count > 40)
    {
        Console.WriteLine($"  ... and {changed.Count - 40} more");
    }

    Console.WriteLine();
}

if (appeared.Count == 0 && vanished.Count == 0 && changed.Count == 0)
{
    Console.WriteLine("The files differ, but they hold exactly the same names in the same counts.");
    Console.WriteLine("That is what an ordinary save looks like: same things saved, different values in them.");
}

return 0;

// Both a file and its folder are accepted, because a save is a folder of four files and the one that
// matters is always called the same thing. Being made to type it is friction for no reason.
static string? Resolve(string path)
{
    if (File.Exists(path)) return path;

    string inFolder = Path.Combine(path, "data.sav");
    return File.Exists(inFolder) ? inFolder : null;
}

static void Report(string title, List<string> names)
{
    if (names.Count == 0) return;

    Console.WriteLine($"{title} ({names.Count}):");
    foreach (string name in names.Take(60))
    {
        Console.WriteLine("  " + name);
    }

    if (names.Count > 60)
    {
        Console.WriteLine($"  ... and {names.Count - 60} more");
    }

    Console.WriteLine();
}
