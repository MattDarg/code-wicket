using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The conversation on screen's transcript is cleared in ONE place, <c>ResetTranscriptItems</c>, which
    /// ends a send still pending before it clears.
    /// </summary>
    /// <remarks>
    /// <para>A SOURCE scan, the <see cref="RenameResidueTests"/> precedent, because what it guards against
    /// is a route added LATER that clears around the check - which no behaviour test of today's routes can
    /// see. Every route that exists today reaches <c>BeginConversationChange</c> first, so the check itself
    /// never fires; the scan is what keeps it the only way to clear.</para>
    /// <para><b>Two scans.</b> Inside <c>ChatViewModel.cs</c> the one <c>Items.Clear()</c> must sit inside
    /// <c>ResetTranscriptItems</c>. Everywhere else under <c>src/</c>, EVERY <c>Items.Clear()</c> is flagged
    /// whatever its receiver - a bare <c>Items</c>, <c>_host.Items</c>, <c>vm.Items</c> - unless a named
    /// allowlist entry says why that line clears something that is not a pending send's transcript. The
    /// receiver is not trusted to say what it is: <c>PromptDelivery</c> reaches the transcript through its own
    /// private <c>Items</c> alias, and the first version of this scan, which matched receivers named like a
    /// view-model, could not see a clear written there (scoped review, 2026-09-14).</para>
    /// <para>What it cannot see, stated: a clear on a line that also holds a trailing comment is read (only
    /// whole comment lines are skipped), and a collection cleared by another name (<c>RemoveAt</c> in a loop,
    /// a <c>Clear</c> through a local of another name) is not. The scan reads text, not types.</para>
    /// </remarks>
    public sealed class TranscriptClearSiteTests
    {
        private const string Mutator = "private void ResetTranscriptItems()";

        // Any `Items.Clear()`: bare, or on any receiver. Not a longer name (`PendingItems.Clear()`).
        private static readonly Regex AnyItemsClear = new(@"(?<![\w])Items\.Clear\(\)", RegexOptions.Compiled);

        /// <summary>
        /// The lines outside <c>ChatViewModel.cs</c> allowed to clear an <c>Items</c>, by file and exact line,
        /// each with why it is not a pending send's transcript. An entry that no longer matches any line
        /// fails the scan, so the list cannot outlive the code it excuses.
        /// </summary>
        private static readonly (string File, string Line, string Reason)[] Allowed =
        {
            ("PerfHarness.cs", "vm.Items.Clear();",
                "the Desktop perf harness resets a pane between measurements; it drives no send, so none is pending"),
            ("ChatView.xaml.cs", "menu.Items.Clear();",
                "a context menu's own items, rebuilt before it opens; not a transcript"),
            ("BatchedObservableCollection.cs", "Items.Clear();",
                "the batched collection clearing its own backing list inside a reset; the type's Items, not a view-model's"),
        };

        [Fact]
        public void TheViewModelClearsItsTranscriptOnlyInsideResetTranscriptItems()
        {
            var file = SourceTree.Files().Single(f => f.Name == "ChatViewModel.cs");
            var code = WithoutCommentLines(File.ReadAllText(file.FullName));

            var clears = AnyItemsClear.Matches(code).Cast<Match>().ToList();
            Assert.True(clears.Count == 1,
                $"ChatViewModel.cs clears an Items collection in {clears.Count} places. Clear the transcript through "
                + "ResetTranscriptItems, which ends a pending send first, and start the route that clears "
                + "with BeginConversationChange.");

            var (open, close) = BodyOf(code, Mutator);
            Assert.True(clears[0].Index > open && clears[0].Index < close,
                "ChatViewModel.cs's one Items.Clear() is not inside ResetTranscriptItems.");
        }

        [Fact]
        public void NoOtherCodeClearsAnItemsCollectionOutsideItsAllowlist()
        {
            var hits = new List<(string File, int Line, string Text)>();
            foreach (var file in SourceTree.Files().Where(f => f.Extension == ".cs"))
            {
                // ChatViewModel.cs has its own scan above; this file names the shapes in order to test the pattern.
                if (file.Name == "ChatViewModel.cs" || file.Name == nameof(TranscriptClearSiteTests) + ".cs")
                    continue;

                var lines = File.ReadAllLines(file.FullName);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!IsCommentLine(lines[i]) && AnyItemsClear.IsMatch(lines[i]))
                        hits.Add((file.Name, i + 1, lines[i].Trim()));
                }
            }

            var stale = Allowed.Where(a => !hits.Any(h => h.File == a.File && h.Text == a.Line))
                .Select(a => $"{a.File}: {a.Line}").ToList();
            Assert.True(stale.Count == 0,
                "An allowlist entry matches nothing, so either the code it excused changed (update the entry) or "
                + "the scan is blind and its clean result is worthless:"
                + Environment.NewLine + string.Join(Environment.NewLine, stale));

            var offenders = hits.Where(h => !Allowed.Any(a => a.File == h.File && a.Line == h.Text))
                .Select(h => $"{h.File}({h.Line}): {h.Text}").ToList();
            Assert.True(offenders.Count == 0,
                "An Items collection is cleared outside ResetTranscriptItems. If it is a pane's transcript, a send "
                + "pending on that pane is not ended first: clear it through the view-model. If it is some other "
                + "collection, add the line to Allowed with the reason:"
                + Environment.NewLine + string.Join(Environment.NewLine, offenders));

            Assert.All(Allowed, a => Assert.False(string.IsNullOrWhiteSpace(a.Reason), a.File + " is allowed with no reason"));
        }

        [Fact]
        public void TheScanSeesEveryReceiver()
        {
            Assert.Matches(AnyItemsClear, "            Items.Clear();");
            Assert.Matches(AnyItemsClear, "            _host.Items.Clear();");
            Assert.Matches(AnyItemsClear, "                vm.Items.Clear();");
            Assert.Matches(AnyItemsClear, "            menu.Items.Clear();");
            Assert.DoesNotMatch(AnyItemsClear, "            PendingItems.Clear();");
            Assert.DoesNotMatch(AnyItemsClear, "            Items.Remove(row);");
        }

        private static bool IsCommentLine(string line)
        {
            var trimmed = line.TrimStart();
            return trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal);
        }

        // Comment lines are blanked rather than removed, so every index stays an index into the file.
        private static string WithoutCommentLines(string code) =>
            string.Join("\n", code.Replace("\r\n", "\n").Split('\n').Select(l => IsCommentLine(l) ? new string(' ', l.Length) : l));

        private static (int Open, int Close) BodyOf(string code, string signature)
        {
            var at = code.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "ChatViewModel.cs has no " + signature + ".");

            var open = code.IndexOf('{', at);
            var depth = 0;
            for (var i = open; i < code.Length; i++)
            {
                if (code[i] == '{')
                    depth++;
                else if (code[i] == '}' && --depth == 0)
                    return (open, i);
            }

            throw new InvalidOperationException("Unbalanced braces after " + signature + ".");
        }
    }
}
