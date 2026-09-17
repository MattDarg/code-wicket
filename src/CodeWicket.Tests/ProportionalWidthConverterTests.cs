using System.Globalization;
using CodeWicket.UI.Converters;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The user bubble's width rule: a fraction of the transcript width, with a floor. The floor is
    /// the point — a bare percentage misbehaves at the narrow end of the range a VS tool window spans
    /// (a ~350px dock). The absence of a ceiling at the wide end is a decision, not an oversight, so
    /// it's pinned here too. Pure arithmetic, no WPF elements — no STA thread needed.
    /// </summary>
    public class ProportionalWidthConverterTests
    {
        // The values ChatView.xaml wires up, so a change to the tuning has to come through here.
        private static ProportionalWidthConverter Converter() =>
            new ProportionalWidthConverter { Fraction = 0.75, Minimum = 320 };

        private static double Convert(double width) =>
            (double)Converter().Convert(width, typeof(double), null!, CultureInfo.InvariantCulture);

        [Fact]
        public void UsesTheFractionAboveTheFloor()
        {
            Assert.Equal(600, Convert(800));
            Assert.Equal(825, Convert(1100));
        }

        [Fact]
        public void NarrowDockFallsBackToTheFloor()
        {
            // 0.75 * 350 = 262: below the floor, so a docked panel lets the bubble run wider than
            // the fraction rather than wrapping every prompt into a ragged column.
            Assert.Equal(320, Convert(350));
        }

        [Fact]
        public void WideWindowKeepsFollowingTheFraction()
        {
            // No ceiling, deliberately. A line-length cap here would bound the user bubble while the
            // assistant column beside it — full panel width, and the longer prose of the two — stayed
            // unbounded: half a readability fix, bought with an inconsistent transcript. A ceiling
            // belongs on the whole column or nowhere (see ChatView.xaml).
            Assert.Equal(1050, Convert(1400));
            Assert.Equal(1920, Convert(2560));
        }

        [Fact]
        public void MaximumStillClampsWhenSet()
        {
            // The bubble leaves it unset, but the knob is part of the converter's contract — it's what
            // a future transcript-wide cap would use.
            var capped = new ProportionalWidthConverter { Fraction = 0.75, Minimum = 320, Maximum = 900 };
            Assert.Equal(900, (double)capped.Convert(2560d, typeof(double), null!, CultureInfo.InvariantCulture));
        }

        [Fact]
        public void UnmeasuredContainerImposesNoConstraint()
        {
            // First layout pass reports 0; clamping to the floor there would visibly snap the
            // bubble on the next pass.
            Assert.Equal(double.PositiveInfinity, Convert(0));
            Assert.Equal(double.PositiveInfinity, Convert(double.NaN));
            Assert.Equal(double.PositiveInfinity, Convert(double.PositiveInfinity));
        }

        [Fact]
        public void NonDoubleValueImposesNoConstraint()
        {
            var result = Converter().Convert("not a width", typeof(double), null!, CultureInfo.InvariantCulture);
            Assert.Equal(double.PositiveInfinity, (double)result);
        }
    }
}
