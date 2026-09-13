using DrovaCoopMod.Versioning;
using Xunit;

namespace DrovaCoopMod.Messages.VersioningTests
{
    /// <summary>
    /// The version policy file is the one thing that can stop a shipped build after the fact, so what
    /// is tested is mostly that it fails open: a line it cannot read must never refuse anyone.
    /// </summary>
    public class VersionPolicyTests
    {
        private static Version V(string text)
        {
            Assert.True(VersionPolicy.TryParseVersion(text, out Version version));
            return version;
        }

        [Fact]
        public void NoFile_AllowsEverything()
        {
            VersionVerdict verdict = VersionPolicy.Parse(null).Judge(V("0.1.30"));

            Assert.True(verdict.Allowed);
            Assert.Null(verdict.Refusal);
            Assert.False(verdict.NewerAvailable);
        }

        [Fact]
        public void Latest_OnlyInforms()
        {
            VersionPolicy policy = VersionPolicy.Parse("latest 0.1.31");

            VersionVerdict old = policy.Judge(V("0.1.30"));
            Assert.True(old.Allowed);
            Assert.True(old.NewerAvailable);
            Assert.Equal(V("0.1.31"), old.Latest);

            Assert.False(policy.Judge(V("0.1.31")).NewerAvailable);
        }

        [Fact]
        public void Minimum_RefusesOlderWithReason()
        {
            VersionPolicy policy = VersionPolicy.Parse("min 0.1.30 saves written by older builds lose quests");

            VersionVerdict refused = policy.Judge(V("0.1.29"));
            Assert.False(refused.Allowed);
            Assert.Contains("0.1.29", refused.Refusal);
            Assert.Contains("lose quests", refused.Refusal);

            Assert.True(policy.Judge(V("0.1.30")).Allowed);
        }

        [Fact]
        public void Block_RefusesExactlyThatBuild()
        {
            VersionPolicy policy = VersionPolicy.Parse("block 0.1.29 corrupts the host's save");

            Assert.False(policy.Judge(V("0.1.29")).Allowed);
            Assert.True(policy.Judge(V("0.1.28")).Allowed);
            Assert.True(policy.Judge(V("0.1.30")).Allowed);
        }

        [Fact]
        public void BlockRange_IncludesBothEnds()
        {
            VersionPolicy policy = VersionPolicy.Parse("block 0.1.25..0.1.27");

            Assert.True(policy.Judge(V("0.1.24")).Allowed);
            Assert.False(policy.Judge(V("0.1.25")).Allowed);
            Assert.False(policy.Judge(V("0.1.26")).Allowed);
            Assert.False(policy.Judge(V("0.1.27")).Allowed);
            Assert.True(policy.Judge(V("0.1.28")).Allowed);
        }

        [Fact]
        public void BlockRange_WrittenBackwards_StillARange()
        {
            VersionPolicy policy = VersionPolicy.Parse("block 0.1.27..0.1.25");

            Assert.False(policy.Judge(V("0.1.26")).Allowed);
        }

        /// <summary>
        /// A named block carries the specific reason, so it wins over the minimum even when both apply.
        /// </summary>
        [Fact]
        public void Block_WinsOverMinimum()
        {
            VersionPolicy policy = VersionPolicy.Parse("min 0.1.30 too old\nblock 0.1.29 the save bug");

            VersionVerdict verdict = policy.Judge(V("0.1.29"));
            Assert.False(verdict.Allowed);
            Assert.Contains("the save bug", verdict.Refusal);
        }

        /// <summary>
        /// The whole point of the file is that a mistake in it must not lock every player out.
        /// </summary>
        [Fact]
        public void UnreadableLines_AreIgnoredAndListed()
        {
            VersionPolicy policy = VersionPolicy.Parse(
                "# a comment\n" +
                "\n" +
                "latest banana\n" +
                "block\n" +
                "unblock 0.1.29\n" +
                "min 0.1.30 # trailing comment\n" +
                "block 0.1.x\n");

            Assert.Equal(4, policy.Ignored.Count);
            Assert.Null(policy.Latest);
            Assert.Equal(V("0.1.30"), policy.Minimum);
            Assert.Empty(policy.Blocked);
            Assert.True(policy.Judge(V("0.1.30")).Allowed);
            Assert.False(policy.Judge(V("0.1.29")).Allowed);
        }

        [Fact]
        public void Keywords_AreCaseInsensitive_AndLinesTrimmed()
        {
            VersionPolicy policy = VersionPolicy.Parse("  LATEST v0.2.0\r\n\tBlock 0.1.29\r\n");

            Assert.Equal(V("0.2.0"), policy.Latest);
            Assert.Single(policy.Blocked);
        }

        [Theory]
        [InlineData("0.1.30", "0.1.30.0")]
        [InlineData("v0.1.30", "0.1.30")]
        [InlineData("0.1.30-beta", "0.1.30")]
        [InlineData("0.1.30+build7", "0.1.30")]
        [InlineData("1.2", "1.2.0.0")]
        [InlineData("3", "3.0")]
        public void Versions_NormalizeToFourComponents(string written, string same)
        {
            Assert.Equal(V(written), V(same));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("banana")]
        [InlineData("0.1.x")]
        [InlineData("-1.0")]
        public void Versions_ThatAreNotVersions_AreRefused(string written)
        {
            Assert.False(VersionPolicy.TryParseVersion(written, out _));
        }

        [Fact]
        public void Format_DropsTheTrailingZero()
        {
            Assert.Equal("0.1.30", VersionPolicy.Format(new Version(0, 1, 30)));
            Assert.Equal("0.1.30", VersionPolicy.Format(new Version(0, 1, 30, 0)));
            Assert.Equal("0.1.30.2", VersionPolicy.Format(new Version(0, 1, 30, 2)));
        }

        [Fact]
        public void Refusal_NamesTheBuildInShortForm()
        {
            VersionVerdict verdict = VersionPolicy.Parse("block 0.1.29").Judge(new Version(0, 1, 29));

            Assert.Contains("build 0.1.29 ", verdict.Refusal);
            Assert.DoesNotContain("0.1.29.0", verdict.Refusal);
        }
    }
}
