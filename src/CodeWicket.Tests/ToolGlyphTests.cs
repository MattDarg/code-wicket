using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Every glyph a tool row can draw is a real glyph, and no two of them are the SAME glyph.
    ///
    /// <para><b>The second half is the load-bearing one, and an injection into the first shows
    /// why.</b> The obvious check is existence — a codepoint the font lacks draws as .notdef, a
    /// hollow box, with no error anywhere — aimed at the failure mode that hand-written hex literals
    /// actually have: two digits transposed. Injected with exactly that (<c>E7E8</c> → <c>E8E7</c>),
    /// it stays green. Segoe MDL2's private-use range is densely
    /// populated, so a transposition does not land on a box; it lands on a DIFFERENT, perfectly valid
    /// icon. Existence cannot see the bug it was written for, which leaves it guarding only a wildly
    /// wrong value — kept, because it is free, but it is not the check.</para>
    ///
    /// <para>Distinctness is, because it is the property the icons are FOR. A session-setup row is told
    /// apart from work by its glyph alone — the status colour being spoken for — so the moment two of
    /// these share a codepoint the distinction is gone and every surface still looks plausible.</para>
    ///
    /// <para><b>The instrument proves it can see before it is trusted.</b> A missing font does not throw
    /// here — <c>Typeface</c> silently falls back — and a fallback font would answer the existence half
    /// with confident nonsense in either direction. So the family is checked first: if that assertion is
    /// the one that fails, the finding is about the machine, not about the glyphs.</para>
    /// </summary>
    public sealed class ToolGlyphTests
    {
        private const string IconFont = "Segoe MDL2 Assets";

        [Fact]
        public void EveryToolRowGlyphIsRealAndDistinct() => StaTest.Run(() =>
        {
            Assert.True(
                new Typeface(IconFont).TryGetGlyphTypeface(out var glyphs),
                $"'{IconFont}' did not resolve to a glyph typeface — this machine, not the glyphs.");
            Assert.Contains(
                "MDL2",
                glyphs.FamilyNames.Values.FirstOrDefault() ?? string.Empty);

            var drawn = new Dictionary<string, string>();
            foreach (var (what, glyph) in EveryGlyph())
            {
                var codepoint = char.ConvertToUtf32(glyph, 0);
                Assert.True(
                    glyphs.CharacterToGlyphMap.ContainsKey(codepoint),
                    $"{what} draws U+{codepoint:X4}, which {IconFont} has no glyph for — it renders as an "
                    + "empty box, silently.");

                Assert.False(
                    drawn.TryGetValue(glyph, out var already),
                    $"{what} draws U+{codepoint:X4}, the same glyph as {already}. These icons are what "
                    + "tells those two rows apart, so sharing one removes the distinction while leaving "
                    + "both rows looking entirely reasonable.");
                drawn[glyph] = what;
            }
        });

        /// <summary>
        /// Read off the view-model rather than restated here, so a glyph added to <c>KindGlyph</c> and
        /// not to this list is the one case the test cannot miss quietly: the kinds are the ACP set plus
        /// the fallback, and the three flags (setup, sub-agent launch, MCP call) outrank them.
        /// <para>
        /// The fallback appears ONCE. Every kind we do not recognise draws it deliberately — <c>other</c>
        /// and an absent kind are the same row to a reader — so listing both would fail distinctness on
        /// the one collision that is the design. Distinctness is a claim about glyphs that are meant to
        /// mean different things.
        /// </para>
        /// </summary>
        private static IEnumerable<(string What, string Glyph)> EveryGlyph()
        {
            foreach (var kind in new[] { "read", "edit", "delete", "search", "execute", "fetch", null })
                yield return ($"kind '{kind ?? "(unrecognised — the fallback)"}'",
                    new ToolItemViewModel("t", "Title", kind).KindGlyph);

            yield return ("a sub-agent launch",
                new ToolItemViewModel("t", "Title", null) { IsSubagentLaunch = true }.KindGlyph);

            // The session opening (issue #217): kiro-cli 2.21.1 starts every session with a turn-less
            // fetch_cloud_config, and this glyph is what says it is not the agent working.
            yield return ("a session-setup call",
                new ToolItemViewModel("t", "Title", "other") { IsSessionSetup = true }.KindGlyph);

            // An MCP call shown by its normalized name draws the @. Its ACP kind is "other", so the
            // glyph it must be told apart from is the fallback gear it drew before.
            yield return ("an MCP tool call",
                new ToolItemViewModel("t", "Title", "other") { RawToolName = "@code-wicket/build_solution" }.KindGlyph);
        }
    }
}
