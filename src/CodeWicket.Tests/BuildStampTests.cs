using System;
using CodeWicket.VSExtension;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Pins the build stamp the About dialog and the tool window's "Starting…" line report
    /// (issue #136 — both used to report <c>File.GetLastWriteTime</c>, i.e. when the file was
    /// INSTALLED, which a deploy makes identical across every file it copies).
    ///
    /// What is worth pinning here is that parsing goes by SHAPE rather than by position: the
    /// metadata after '+' is an unordered set in semver, the SDK appends the commit itself, and a
    /// future identifier must not be able to shift the meaning of the ones already there. Plus the
    /// two degradations the callers rely on — an unstamped binary must parse to "no build time"
    /// rather than to a wrong one, since that null is the cue to fall back and RELABEL.
    ///
    /// The parser is compiled into the net472 VSIX (the dialog holds no runtime dependency on our
    /// other assemblies on purpose) and linked into this project as source; see the .csproj.
    /// </summary>
    public sealed class BuildStampTests
    {
        // What src\BuildStamp.targets actually emits, verified against the built binaries.
        private const string Stamped = "0.1.0+972afa531278f6cfe0fafe46ea9c7ad1fb978fac.20260813T2201Z";

        [Fact]
        public void ReadsVersionCommitAndBuildTime()
        {
            var stamp = BuildStamp.Parse(Stamped);

            Assert.Equal("0.1.0", stamp.Version);
            Assert.Equal("972afa531278f6cfe0fafe46ea9c7ad1fb978fac", stamp.Commit);
            Assert.Equal("972afa5", stamp.ShortCommit);
            Assert.Equal(new DateTime(2026, 8, 13, 22, 1, 0, DateTimeKind.Utc), stamp.BuiltUtc);
            Assert.False(stamp.ContinuousIntegration);
        }

        [Fact]
        public void DescribesTheBuildAsUtcWithItsCommit()
        {
            Assert.Equal("2026-08-13 22:01 UTC (972afa5)", BuildStamp.Parse(Stamped).Describe());
        }

        [Fact]
        public void MarksACiBuild()
        {
            var stamp = BuildStamp.Parse(Stamped + ".ci");

            Assert.True(stamp.ContinuousIntegration);
            Assert.Equal("2026-08-13 22:01 UTC (972afa5, CI)", stamp.Describe());
        }

        /// <summary>
        /// The identifiers are a SET, not a sequence: the SDK owns the commit's position and we
        /// append after it, but nothing in semver says that order is fixed and a third identifier
        /// could land anywhere. Reading by position would make a reordering silently report the
        /// commit as the build time (or, worse, parse the wrong half and look right).
        /// </summary>
        [Fact]
        public void ReadsIdentifiersInAnyOrder()
        {
            var stamp = BuildStamp.Parse("0.1.0+20260813T2201Z.ci.972afa531278f6cfe0fafe46ea9c7ad1fb978fac");

            Assert.Equal("972afa531278f6cfe0fafe46ea9c7ad1fb978fac", stamp.Commit);
            Assert.Equal(new DateTime(2026, 8, 13, 22, 1, 0, DateTimeKind.Utc), stamp.BuiltUtc);
            Assert.True(stamp.ContinuousIntegration);
        }

        /// <summary>An identifier we don't know is ignored, not mistaken for one we do.</summary>
        [Fact]
        public void IgnoresUnknownIdentifiers()
        {
            var stamp = BuildStamp.Parse(Stamped + ".rc2.build-4711");

            Assert.Equal("972afa531278f6cfe0fafe46ea9c7ad1fb978fac", stamp.Commit);
            Assert.Equal(new DateTime(2026, 8, 13, 22, 1, 0, DateTimeKind.Utc), stamp.BuiltUtc);
            Assert.False(stamp.ContinuousIntegration);
        }

        /// <summary>
        /// The pre-#136 shape, which every already-installed build carries: a version and a commit,
        /// no build time. Describe() must return null so the caller falls back to the install time
        /// and labels it as one, rather than presenting it as a build time — which IS the bug.
        /// </summary>
        [Fact]
        public void AnUnstampedBuildHasNoBuildTime()
        {
            var stamp = BuildStamp.Parse("0.1.0+972afa531278f6cfe0fafe46ea9c7ad1fb978fac");

            Assert.Equal("0.1.0", stamp.Version);
            Assert.Equal("972afa5", stamp.ShortCommit);
            Assert.Null(stamp.BuiltUtc);
            Assert.Null(stamp.Describe());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("0.1.0")]
        [InlineData("0.1.0+")]
        [InlineData("0.1.0+nonsense")]
        public void NeverThrowsAndNeverInventsABuildTime(string? informational)
        {
            var stamp = BuildStamp.Parse(informational);

            Assert.NotNull(stamp.Version);
            Assert.Null(stamp.Describe());
        }

        /// <summary>
        /// A build time is UTC at the point it is stamped, so it must be read as UTC wherever the
        /// dialog is opened. Parsed as local, a stamp would be shifted by the reader's own offset
        /// and then labelled "UTC" — wrong in a way nobody in the build's timezone could see.
        /// </summary>
        [Fact]
        public void ReadsTheBuildTimeAsUtcRegardlessOfTheLocalZone()
        {
            var built = BuildStamp.Parse(Stamped).BuiltUtc;

            Assert.NotNull(built);
            Assert.Equal(DateTimeKind.Utc, built!.Value.Kind);
            Assert.Equal(new DateTime(2026, 8, 13, 22, 1, 0, DateTimeKind.Utc), built);
        }

        /// <summary>A short hex-looking token is not a commit; the length floor is what stops
        /// something like a build number being reported as the source it was built from.</summary>
        [Fact]
        public void DoesNotTakeAShortHexTokenForACommit()
        {
            Assert.Null(BuildStamp.Parse("0.1.0+abc.20260813T2201Z").Commit);
        }

        /// <summary>The reader for the bundled engine: no such file is not an exception, and not a
        /// stamp either — the caller distinguishes "not found" from "built at …".</summary>
        [Fact]
        public void AMissingFileHasNoStamp()
        {
            Assert.Null(BuildStamp.ForFile(@"C:\code-wicket\no-such-engine.exe"));
            Assert.Null(BuildStamp.ForFile(null));
        }
    }
}
