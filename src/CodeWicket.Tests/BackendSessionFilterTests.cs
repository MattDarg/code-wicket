using System;
using CodeWicket.Core;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Recognising a conversation the backend created and nothing was ever said in (issue #108
    /// follow-on) — the rule on its own, away from the picker that applies it.
    /// <para>Neither backend reports a message count, so this cannot be asked directly: what
    /// <c>session/list</c> carries is a title, an <c>updatedAt</c> and — on Kiro only — a
    /// <c>_meta.kiro.createdAt</c>. The rule is therefore two weak signals that must AGREE, and the
    /// tests below are mostly about the disagreements.</para>
    /// </summary>
    public sealed class BackendSessionFilterTests
    {
        private static readonly DateTimeOffset Created = new(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);

        [Fact]
        public void APlaceholderTitleWithNoGapIsUnused()
        {
            // Kiro v3's own wire, measured 2026-09-03: "New Session", 16 ms between the two stamps.
            Assert.True(BackendSessionFilter.IsUnused(
                "New Session", Created, Created.AddMilliseconds(16)));
        }

        [Fact]
        public void ANamedConversationIsNeverUnused()
        {
            // Even with the stamps a breath apart. A short exchange looks exactly like an untouched
            // session on the clock, and only the title separates them — so a rule resting on the gap
            // alone hides real conversations, and a merely shorter list looks like nothing went wrong.
            Assert.False(BackendSessionFilter.IsUnused(
                "Quick question about the build", Created, Created.AddMilliseconds(40)));
        }

        [Fact]
        public void APlaceholderTitleWithREALACTIVITYIsNotUnused()
        {
            // The other direction, and the one that keeps the title honest. If a backend ever leaves a
            // used conversation on its placeholder name — a rename that failed, a session named only
            // at turn end — the clock is what says it was used.
            Assert.False(BackendSessionFilter.IsUnused(
                "New Session", Created, Created.AddMinutes(20)));
        }

        [Fact]
        public void NoCreationTimeHidesNothing()
        {
            // Claude's whole behaviour. The adapter drops createdAt before the wire, so there is no
            // second opinion — and this REFUSES rather than falling back to the title, which alone is
            // a display string deciding on its own. Not a gap to be filled later: it is the rule.
            Assert.False(BackendSessionFilter.IsUnused("New Session", null, Created));
            Assert.False(BackendSessionFilter.IsUnused(null, null, Created));
        }

        [Fact]
        public void NoUpdateTimeHidesNothing()
        {
            Assert.False(BackendSessionFilter.IsUnused("New Session", Created, null));
        }

        [Fact]
        public void AnAbsentTitleCountsAsAPlaceholder()
        {
            // "The backend never named this" is the same signal in its strongest form. It is only safe
            // because of the AND above: Claude leaves a title absent until its summary has been
            // generated, and Claude reports no creation time at all, so no Claude row reaches here.
            Assert.True(BackendSessionFilter.IsUnused(null, Created, Created));
            Assert.True(BackendSessionFilter.IsUnused("   ", Created, Created));
        }

        [Fact]
        public void ThePlaceholderIsMatchedTrimmedAndCaseInsensitively()
        {
            Assert.True(BackendSessionFilter.IsPlaceholderTitle("  new session  "));
            Assert.False(BackendSessionFilter.IsPlaceholderTitle("New Session plan"));
        }

        [Fact]
        public void StampsInTheWrongOrderStillCountAsNoGap()
        {
            // A negative gap is a clock disagreeing with itself, not twenty minutes of work. Taken as
            // signed, it sails under the threshold and reads as unused anyway — so this pins the
            // absolute rather than the accident that gives the same answer.
            Assert.True(BackendSessionFilter.IsUnused(
                "New Session", Created, Created.AddMilliseconds(-16)));
            Assert.False(BackendSessionFilter.IsUnused(
                "New Session", Created, Created.AddMinutes(-20)));
        }
    }
}
