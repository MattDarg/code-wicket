using System;
using System.IO;
using System.Linq;
using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// What the two sides of a diff are called on disk (<see cref="DiffScratchNames"/>).
    /// <para>
    /// Named from the leaf alone, two cards in one turn collided: a turn editing
    /// <c>ProjectA\Program.cs</c> and <c>ProjectB\Program.cs</c> wrote both to the same two scratch
    /// paths. The host keeps up to sixteen diff windows live and matches them on path AND content, so
    /// the second card does not reactivate the first — it rewrites the files under it. Promote A's
    /// diff to a permanent tab, click B, and VS's file-change tracking reloads window A showing B's
    /// text under A's caption; where the open window holds its backing files instead, the write throws
    /// a sharing violation into the outer catch and the click silently does nothing.
    /// </para>
    /// <para>
    /// These live in Core because the naming rule is a statement about a string, and the four
    /// properties that decide whether it works can all be asked without Visual Studio. The half that
    /// cannot — that the host actually opens the files it was handed — still needs a live Visual Studio instance.
    /// </para>
    /// </summary>
    public sealed class DiffScratchNameTests
    {
        /// <summary>The bug: same leaf, different projects, must not share a scratch file.</summary>
        [Fact]
        public void SameLeafInDifferentDirectoriesGetsDifferentNames()
        {
            var a = DiffScratchNames.For(@"C:\ws\ProjectA\Program.cs");
            var b = DiffScratchNames.For(@"C:\ws\ProjectB\Program.cs");

            Assert.NotEqual(a.Before, b.Before);
            Assert.NotEqual(a.After, b.After);
        }

        /// <summary>
        /// ...and the same file must agree with itself, which is the constraint that rules out a GUID.
        /// Re-opening a diff rewrites its two files rather than adding a pair, and the host's
        /// reactivation path assumes the names are stable.
        /// </summary>
        [Fact]
        public void TheSameFileAlwaysGetsTheSameNames()
        {
            Assert.Equal(
                DiffScratchNames.For(@"C:\ws\ProjectA\Program.cs"),
                DiffScratchNames.For(@"C:\ws\ProjectA\Program.cs"));
        }

        /// <summary>
        /// Stable across PROCESSES too, not merely within one — which is why this cannot be
        /// <c>string.GetHashCode</c>, whose seed is randomised per process. A per-launch name would
        /// leave the scratch directory filling with orphans nothing ever rewrites.
        /// </summary>
        [Fact]
        public void TheNameIsNotProcessDependent()
        {
            // The literal is the answer this input produced when the rule was written. It is allowed to
            // change deliberately (the scratch directory is disposable) — but not to differ run to run,
            // and a diff here is the signal that the hash stopped being stable rather than that it was
            // retuned.
            Assert.Equal("Program.2e5d5f9a.before.cs", DiffScratchNames.For(@"C:\ws\ProjectA\Program.cs").Before);
        }

        /// <summary>Windows spells paths case-insensitively, so two spellings are one file, not two.</summary>
        [Fact]
        public void DirectoryCaseDoesNotSplitOneFileIntoTwo()
        {
            Assert.Equal(
                DiffScratchNames.For(@"C:\WS\PROJECTA\Program.cs").Before,
                DiffScratchNames.For(@"C:\ws\projecta\Program.cs").Before);
        }

        /// <summary>
        /// The extension survives, and that is a feature rather than cosmetics: it is what gives each
        /// side syntax highlighting in the diff window, and the reason the original name was built from
        /// the stem at all.
        /// </summary>
        [Theory]
        [InlineData(@"C:\ws\a\Program.cs", ".cs")]
        [InlineData(@"C:\ws\a\build.props", ".props")]
        [InlineData(@"C:\ws\a\notes.md", ".md")]
        public void TheExtensionIsPreserved(string path, string ext)
        {
            var (before, after) = DiffScratchNames.For(path);

            Assert.EndsWith(".before" + ext, before, StringComparison.Ordinal);
            Assert.EndsWith(".after" + ext, after, StringComparison.Ordinal);
        }

        /// <summary>The two sides are distinguishable, or the diff compares a file with itself.</summary>
        [Fact]
        public void TheTwoSidesDiffer()
        {
            var (before, after) = DiffScratchNames.For(@"C:\ws\a\Program.cs");

            Assert.NotEqual(before, after);
        }

        /// <summary>
        /// The result is a legal, bounded FILE NAME — no directory separators, no invalid characters,
        /// and short enough that the scratch directory plus this cannot approach a path limit. Fixing a
        /// collision by lengthening the name is an easy way to trade one silent failure for another.
        /// </summary>
        [Theory]
        [InlineData(@"C:\ws\a\Program.cs")]
        [InlineData(@"C:\ws\a\A really quite long source file name that goes on and on and on.cs")]
        [InlineData(@"C:\ws\a\no-extension")]
        public void TheNamesAreLegalAndBounded(string path)
        {
            var (before, after) = DiffScratchNames.For(path);

            foreach (var name in new[] { before, after })
            {
                Assert.DoesNotContain(Path.DirectorySeparatorChar, name);
                Assert.DoesNotContain(Path.AltDirectorySeparatorChar, name);
                Assert.DoesNotContain(name, c => Path.GetInvalidFileNameChars().Contains(c));
                Assert.True(name.Length <= DiffScratchNames.MaxLeafLength + 32, $"'{name}' is {name.Length} chars");
            }
        }

        /// <summary>
        /// Degenerate inputs still yield a usable name rather than a bare dot or an exception. This is
        /// called from a click handler whose only failure mode is doing nothing at all.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData(@"C:\ws\a\.gitignore")]
        [InlineData(@"C:\ws\a\")]
        public void ADegenerateNameStillProducesSomethingUsable(string path)
        {
            var (before, after) = DiffScratchNames.For(path);

            Assert.NotEqual(".before", before);
            Assert.False(string.IsNullOrWhiteSpace(before));
            Assert.False(string.IsNullOrWhiteSpace(after));
            Assert.NotEqual(before, after);
        }
    }
}
