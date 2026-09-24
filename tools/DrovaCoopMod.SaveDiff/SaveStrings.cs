using System.Text;
using System.Text.RegularExpressions;

namespace DrovaCoopMod.SaveDiff
{
    /// <summary>
    /// Pulls the readable names out of a save file.
    ///
    /// Drova's save is a binary blob written by a serializer that stores type and field names inline as
    /// UTF-16, uncompressed - they are legible in a hex dump, which is what makes this approach work at all.
    /// Everything around them is length prefixes, numbers and pointers, and none of that has to be
    /// understood to answer the question this tool exists for.
    ///
    /// Names rather than bytes, because bytes shift. A save taken a second later holds a different play
    /// time and a different player position, and any change in the length of anything moves everything
    /// after it - so a byte comparison of two legitimate saves reports that they differ from some early
    /// offset onwards, which is true and useless. The set of names in a save does not move: two saves of
    /// the same world hold the same names in the same counts however far the player walked between them.
    /// A name that appears in one and not the other is something that was saved which was not saved before.
    /// </summary>
    internal static class SaveStrings
    {
        /// <summary>
        /// Below this, matches are mostly coincidence: two adjacent bytes of a float can read as a letter
        /// followed by a zero, and at three characters there are thousands of those in a megabyte of data.
        /// </summary>
        private const int MinimumLength = 4;

        /// <summary>
        /// A run of the characters a type name, a field name or a save key is made of. Deliberately
        /// narrow: allowing all of printable ASCII pulls in punctuation that occurs inside numbers and
        /// turns half the file into short false runs.
        /// </summary>
        private static readonly Regex Readable = new(@"[A-Za-z0-9_.\-+#/\[\]`, ]{" + MinimumLength + ",}", RegexOptions.Compiled);

        /// <summary>
        /// Counts every readable name in a save.
        /// </summary>
        /// <param name="bytes">The whole file.</param>
        /// <returns>Each name found, and how many times it occurred.</returns>
        internal static Dictionary<string, int> Count(byte[] bytes)
        {
            Dictionary<string, int> counts = new(StringComparer.Ordinal);

            foreach (string found in Utf16Runs(bytes))
            {
                counts[found] = counts.TryGetValue(found, out int seen) ? seen + 1 : 1;
            }

            return counts;
        }

        /// <summary>
        /// Walks the file for runs of printable characters stored as UTF-16.
        ///
        /// Both alignments are scanned. A run can start on an odd offset as easily as an even one - the
        /// serializer packs strings after variable-length prefixes and owes us no alignment - and scanning
        /// only the even offsets silently halves what is found.
        /// </summary>
        private static IEnumerable<string> Utf16Runs(byte[] bytes)
        {
            for (int start = 0; start < 2 && start < bytes.Length; start++)
            {
                // Every code unit decodes to exactly one char, a lone surrogate included, and the class
                // only admits chars whose high byte is zero, so this matches byte pairs one to one.
                string text = Encoding.Unicode.GetString(bytes, start, (bytes.Length - start) & ~1);

                foreach (Match match in Readable.Matches(text))
                {
                    yield return match.Value;
                }
            }
        }
    }
}
