using System;
using System.Collections.Generic;
using System.Linq;
using CodeWicket.UI.Input;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// What a drag actually carries, decoded. Explorer publishes a clean <c>string[]</c>; Visual Studio's
    /// Solution Explorer may publish only its own <c>|</c>-delimited project-item blob, whose layout is
    /// undocumented — so the rule is "keep what is real on disk" rather than "parse position N".
    /// </summary>
    /// <remarks>
    /// Every case here drives <see cref="FileDropPaths.Filter"/> with a set-backed predicate, so the whole
    /// rule is exercised without touching the filesystem — which also means these tests say nothing about
    /// the machine they run on.
    /// </remarks>
    public sealed class FileDropPathsTests
    {
        private const string Root = @"C:\ws\dotnet";

        private static readonly HashSet<string> OnDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\ws\dotnet\proj\Foo.cs",
            @"C:\ws\dotnet\proj\Bar.cs",
            @"C:\ws\dotnet\proj\Baz.cs",
            @"C:\ws\dotnet\assets",
            @"C:\other\Outside.cs",

            // Real on disk, and never dragged. Solution Explorer's project-item blob carries the
            // containing project file beside the item, so this is what the tier rule exists to exclude.
            // A set WITHOUT it lets that test pass over the very bug it was written for — which is how it
            // was written first, and what proving it load-bearing caught.
            @"C:\ws\dotnet\proj\MyProject.csproj",
        };

        private static IReadOnlyList<string> Filter(params string?[] tokens)
            => FileDropPaths.Filter(tokens, OnDisk.Contains);

        // ---- what a drag is CLAIMED as, before anything is decoded ------------------------------

        private static System.Windows.DataObject Data(params string[] formats)
        {
            // autoConvert FALSE on the way in as well as on the way out. A converting DataObject
            // publishes FileDrop for a FileNameW string all by itself, and the case under test is
            // precisely a source that did NOT offer CF_HDROP — so a converting stub would make every
            // one of these drags look like a file drag and the checks would say nothing.
            var data = new System.Windows.DataObject();
            foreach (var format in formats)
                data.SetData(
                    format,
                    format == System.Windows.DataFormats.FileDrop
                        ? new[] { @"C:\ws\dotnet\proj\Foo.cs" }
                        : (object)@"C:\ws\dotnet\proj\Foo.cs",
                    autoConvert: false);
            return data;
        }

        /// <summary>
        /// A single-file format offered BESIDE text is a provenance note, not a drag — only the presence
        /// of the text tells "these are the dragged files" from "this is where the dragged text came
        /// from", which is the same principle as the tier itself: the GESTURE decides.
        /// </summary>
        /// <remarks>
        /// Named for the case that motivated it, which is <b>unconfirmed</b> — see the remarks on
        /// <c>FileDropPaths.CouldCarryFiles</c>. Word and Outlook were both dragged into the composer on
        /// UNFIXED code, where a bare <c>FileNameW</c> was enough to insert the path, and both gave the
        /// text (2026-09-08). What this pins is the FORMAT RULE, which is sound whoever publishes the
        /// formats; it is not evidence that any Office build does.
        /// </remarks>
        [Fact]
        public void ASingleFileFormatBesideTextIsNotClaimedAsAFileDrag()
        {
            Assert.False(FileDropPaths.CouldCarryFiles(
                Data("FileNameW", "FileName", System.Windows.DataFormats.UnicodeText)));
        }

        /// <summary>The same formats with nothing dragged as text ARE a single-file drag, as before.</summary>
        [Fact]
        public void TheShellSingleFileFormatsAloneAreStillAFileDrag()
        {
            Assert.True(FileDropPaths.CouldCarryFiles(Data("FileNameW", "FileName")));
        }

        /// <summary>
        /// A real file drag is untouched, and this is the half that must not regress: CF_HDROP means a
        /// file was dragged whatever else rides along, so text beside it changes nothing.
        /// </summary>
        [Fact]
        public void AnExplorerFileDragIsClaimedEvenWithTextAlongside()
        {
            Assert.True(FileDropPaths.CouldCarryFiles(
                Data(System.Windows.DataFormats.FileDrop, "FileNameW", System.Windows.DataFormats.UnicodeText)));
        }

        /// <summary>Solution Explorer's shape, likewise — the VS pair is a drag, not a provenance note.</summary>
        [Fact]
        public void ASolutionExplorerDragIsClaimedEvenWithTextAlongside()
        {
            Assert.True(FileDropPaths.CouldCarryFiles(
                Data("CF_VSSTGPROJECTITEMS", System.Windows.DataFormats.UnicodeText)));
        }

        [Fact]
        public void APlainTextDragIsNotAFileDrag()
        {
            Assert.False(FileDropPaths.CouldCarryFiles(Data(System.Windows.DataFormats.UnicodeText)));
        }

        /// <summary>The ordinary case: a Windows Explorer multi-select, already one path per entry.</summary>
        [Fact]
        public void ExplorerFileDrop_KeepsEveryRealPath()
        {
            Assert.Equal(
                new[] { @"C:\ws\dotnet\proj\Foo.cs", @"C:\ws\dotnet\proj\Bar.cs" },
                Filter(@"C:\ws\dotnet\proj\Foo.cs", @"C:\ws\dotnet\proj\Bar.cs"));
        }

        /// <summary>
        /// The VS blob. Its leading fields are a project guid and a display name, and reading the layout by
        /// position would put both of them in the user's prompt as though they were files. Keeping only
        /// what exists on disk needs no knowledge of the layout at all.
        /// </summary>
        [Fact]
        public void VsBlob_SplitsOnPipeAndKeepsOnlyRealPaths()
        {
            const string blob =
                "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}|MyProject.csproj|" +
                @"C:\ws\dotnet\proj\Foo.cs";

            Assert.Equal(new[] { @"C:\ws\dotnet\proj\Foo.cs" }, Filter(blob));
        }

        /// <summary>
        /// A relative token is REFUSED even where one might exist, because "exists" and every later
        /// resolution would both be measured against the process working directory — devenv's install
        /// folder in the VS host, a spelling nothing else in the product uses. This is the other end of
        /// <c>WorkspacePath.ForPrompt</c>'s contract; without it a drop can hand the agent a path that
        /// resolves, to a file nobody named.
        /// </summary>
        [Fact]
        public void RelativeTokens_AreRefused()
        {
            var everythingExists = FileDropPaths.Filter(
                new[] { @"proj\Foo.cs", "Foo.cs", @"..\Foo.cs" },
                _ => true);

            Assert.Empty(everythingExists);
        }

        /// <summary>
        /// Nothing real means nothing inserted. The alternative — inserting a decoded artefact we could not
        /// verify — puts a guid in the prompt and reads as the feature working.
        /// </summary>
        /// <remarks>
        /// The ROOTED ghost is the load-bearing half: a guid and a bare project name are already refused
        /// for not being rooted, so a test of those alone passes with the existence check removed
        /// entirely. A token has to get past every earlier gate before it
        /// can pin the last one.
        /// </remarks>
        [Fact]
        public void NothingRealOnDisk_YieldsNothing()
        {
            Assert.Empty(Filter(@"C:\ws\dotnet\proj\Ghost.cs"));
            Assert.Empty(Filter("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}|MyProject.csproj"));
            Assert.Empty(Filter(null, "", "   "));
        }

        /// <summary>
        /// A drag commonly publishes the same file in more than one format, and the two are read one after
        /// the other. Without the dedupe, one dropped file arrives in the prompt twice.
        /// </summary>
        [Fact]
        public void SameFileInTwoFormats_AppearsOnce()
        {
            Assert.Equal(
                new[] { @"C:\ws\dotnet\proj\Foo.cs" },
                Filter(@"C:\ws\dotnet\proj\Foo.cs", @"C:\ws\dotnet\proj\Foo.cs"));
        }

        /// <summary>
        /// Order is the user's selection order, and it is the only thing telling them the drop was
        /// understood. A sort would silently rearrange a deliberate sequence.
        /// </summary>
        [Fact]
        public void MultiSelectOrder_IsPreserved()
        {
            Assert.Equal(
                new[] { @"C:\ws\dotnet\proj\Baz.cs", @"C:\ws\dotnet\proj\Foo.cs", @"C:\ws\dotnet\proj\Bar.cs" },
                Filter(@"C:\ws\dotnet\proj\Baz.cs", @"C:\ws\dotnet\proj\Foo.cs", @"C:\ws\dotnet\proj\Bar.cs"));
        }

        /// <summary>
        /// A folder is a legitimate thing to point an agent at, and Solution Explorer has folders. Filter on
        /// <c>File.Exists</c> alone and that drop silently does nothing at all.
        /// </summary>
        /// <remarks>
        /// <b>This one runs against the REAL filesystem, deliberately.</b> Every other case here injects its
        /// own <c>exists</c> predicate, which is what keeps them fast and machine-independent — but it also
        /// means they cannot see the default predicate at all, and the folder rule lives nowhere else.
        /// Written with the injected predicate first, this test passed with the product narrowed to
        /// <c>File.Exists</c>: it was pinning the harness, not the rule.
        /// </remarks>
        [Fact]
        public void ARealDirectory_IsKeptByTheDefaultPredicate()
        {
            var dir = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "cwkt-drop-" + System.Guid.NewGuid().ToString("N"));
            var file = System.IO.Path.Combine(dir, "Foo.cs");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(file, "// dropped");
            try
            {
                // No predicate passed: this is the code path a real drop takes.
                Assert.Equal(new[] { dir }, FileDropPaths.Filter(new[] { dir }));
                Assert.Equal(new[] { file, dir }, FileDropPaths.Filter(new[] { file, dir }));
                Assert.Empty(FileDropPaths.Filter(new[] { System.IO.Path.Combine(dir, "Ghost.cs") }));
            }
            finally
            {
                System.IO.Directory.Delete(dir, recursive: true);
            }
        }

        /// <summary>
        /// Quotes and stray whitespace are unwrapped — a source that pre-quotes its path would otherwise
        /// fail the existence check and vanish, which looks exactly like an unsupported format.
        /// </summary>
        [Fact]
        public void QuotedAndPaddedTokens_AreUnwrapped()
        {
            Assert.Equal(
                new[] { @"C:\ws\dotnet\proj\Foo.cs" },
                Filter("  \"" + @"C:\ws\dotnet\proj\Foo.cs" + "\"  "));
        }

        /// <summary>
        /// A path outside the workspace survives the decode. Deciding it is "not ours" belongs to the
        /// spelling rule, which keeps it absolute so it stays obvious — not to the decode, which would just
        /// make the drop do nothing.
        /// </summary>
        [Fact]
        public void APathOutsideTheWorkspace_IsStillAFile()
        {
            Assert.Equal(new[] { @"C:\other\Outside.cs" }, Filter(@"C:\other\Outside.cs"));
        }

        /// <summary>
        /// The Solution Explorer regression, measured live on 2026-08-25 (#62).
        /// </summary>
        /// <remarks>
        /// VS publishes <c>CF_HDROP</c> AND <c>CF_VSSTGPROJECTITEMS</c> for one drag, and the blob carries
        /// the containing <c>.csproj</c> beside the item. Both exist on disk, so "keep whatever is real"
        /// kept both and ONE dropped file put TWO paths in the composer. Existence answers "is this a
        /// path"; only the format answers "was this dragged".
        /// </remarks>
        [Fact]
        public void WhenAFileFormatNamesTheDrag_TheProjectBlobIsNotConsulted()
        {
            var kept = FileDropPaths.FilterTiered(
                authoritative: new[] { @"C:\ws\dotnet\proj\Foo.cs" },
                fallback: new[] { "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}|" + @"C:\ws\dotnet\proj\MyProject.csproj|C:\ws\dotnet\proj\Foo.cs" },
                exists: OnDisk.Contains);

            // Not merely deduped: the .csproj is a DIFFERENT real path, present in the fake filesystem
            // above precisely so that reading both tiers would produce two entries here — which is what
            // the user saw, as `kept=2` for one dropped file.
            Assert.Equal(new[] { @"C:\ws\dotnet\proj\Foo.cs" }, kept);
        }

        /// <summary>
        /// And the fallback still earns its place: a drag offering ONLY the project blob is the case the
        /// blob decoding was written for, and skipping it outright would make that drop do nothing.
        /// </summary>
        [Fact]
        public void WithNoFileFormat_TheProjectBlobIsStillRead()
        {
            var kept = FileDropPaths.FilterTiered(
                authoritative: new string?[0],
                fallback: new[] { "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}|MyProject.csproj|" + @"C:\ws\dotnet\proj\Foo.cs" },
                exists: OnDisk.Contains);

            Assert.Equal(new[] { @"C:\ws\dotnet\proj\Foo.cs" }, kept);
        }

        /// <summary>
        /// The Explorer regression, measured live on 2026-09-11: one dragged file put two paths in the
        /// composer. Explorer offers all three shell file formats at once, and the third is ANSI, so it
        /// carries the 8.3 SHORT name — a different string naming the same file, which is why existence
        /// kept it and the dedupe could not see it.
        /// <para>The <c>read=[FileDrop,FileNameW,FileName] tokens=3 kept=2</c> line in engine.log is
        /// what this pins; the same shape as the Solution Explorer tier one level in.</para>
        /// </summary>
        [Fact]
        public void WhenTheShellOffersAllThreeFileFormats_OnlyTheDragFormatIsRead()
        {
            var data = new System.Windows.DataObject();
            data.SetData(System.Windows.DataFormats.FileDrop, new[] { LongName }, autoConvert: false);
            data.SetData("FileNameW", LongName, autoConvert: false);
            data.SetData("FileName", ShortName, autoConvert: false);

            Assert.Equal(new[] { LongName }, FileDropPaths.Decode(data, BothSpellings.Contains));
        }

        /// <summary>
        /// And the single-file formats still earn their place — a source that offers no <c>CF_HDROP</c>
        /// must still drop — with the same rule applied between the pair: the Unicode spelling wins, so
        /// the ANSI short name is never reached. That is what makes this fix need no path
        /// canonicalization, which would be a filesystem round-trip per token.
        /// </summary>
        [Fact]
        public void WithNoDragFormat_TheUnicodeNameWinsOverTheAnsiShortName()
        {
            var data = new System.Windows.DataObject();
            data.SetData("FileNameW", LongName, autoConvert: false);
            data.SetData("FileName", ShortName, autoConvert: false);

            Assert.Equal(new[] { LongName }, FileDropPaths.Decode(data, BothSpellings.Contains));
        }

        /// <summary>
        /// The ANSI format alone is still read: it is the last tier, not a banned one.
        /// </summary>
        [Fact]
        public void WithOnlyTheAnsiName_ItIsStillRead()
        {
            var data = new System.Windows.DataObject();
            data.SetData("FileName", ShortName, autoConvert: false);

            Assert.Equal(new[] { ShortName }, FileDropPaths.Decode(data, BothSpellings.Contains));
        }

        private const string LongName = @"C:\ws\dotnet\proj\control-100x200.png";
        private const string ShortName = @"C:\ws\dotnet\proj\CONTRO~1.PNG";

        /// <summary>
        /// BOTH spellings exist here, and that is what lets the tests above fail. They are one file,
        /// they are different strings, and no dedupe can tell — so a set holding only the long name
        /// would pass with every format read, which is the <c>MyProject.csproj</c> lesson one test up.
        /// </summary>
        private static readonly HashSet<string> BothSpellings =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { LongName, ShortName };

        /// <summary>
        /// The decode and the spelling rule compose into what actually lands in the composer. Pinned end to
        /// end because that join is the feature, and each half can be right while the pair is wrong.
        /// </summary>
        [Fact]
        public void DecodedPathsAreSpelledForThePromptAgainstTheAgentRoot()
        {
            var spelled = Filter(@"C:\ws\dotnet\proj\Foo.cs", @"C:\other\Outside.cs")
                .Select(p => CodeWicket.Core.Ide.WorkspacePath.ForPrompt(p, Root));

            Assert.Equal(new[] { "proj/Foo.cs", @"C:\other\Outside.cs" }, spelled);
        }
    }
}
