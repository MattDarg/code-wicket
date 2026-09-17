using System;
using System.Globalization;
using System.Windows.Data;

namespace CodeWicket.UI.Converters
{
    /// <summary>
    /// Turns a container's width into a clamped fraction of it: <c>clamp(Fraction * width, Minimum, Maximum)</c>.
    /// Used for the user message bubble's MaxWidth, where a bare percentage needs a floor —
    /// a VS tool window spans everything from a ~350px dock to a floated 4K window:
    ///   * the fraction leaves a right-hand gutter, which is what actually reads as "this message is mine",
    ///     and it stays proportional at every width;
    ///   * <see cref="Minimum"/> keeps a narrow dock usable (75% of 350px wraps every prompt into a ragged column,
    ///     and down there the right alignment + background already carry the distinction alone).
    /// <see cref="Maximum"/> is left unset by the bubble (default: unbounded) — see ChatView.xaml for why a
    /// line-length ceiling belongs on the whole transcript column or nowhere. The knob stays here for that.
    /// Bind to an element INSIDE the zoom LayoutTransform, so the bounds stay in pre-zoom units and scale
    /// with Ctrl+MouseWheel the way a fixed MaxWidth did. Note the consequence for any future ceiling: the
    /// font size is pre-zoom too, so a bound expressed here is a constant characters-per-line at every zoom
    /// level, whereas the unbounded fraction's line length grows as the user zooms out.
    /// Bind to an element INSIDE the zoom LayoutTransform, so the bounds stay in pre-zoom units and scale
    /// with Ctrl+MouseWheel the way the old fixed MaxWidth did.
    /// </summary>
    public sealed class ProportionalWidthConverter : IValueConverter
    {
        /// <summary>Fraction of the container's width to use, before clamping.</summary>
        public double Fraction { get; set; } = 0.75;

        /// <summary>Lower bound, in device-independent pixels.</summary>
        public double Minimum { get; set; }

        /// <summary>Upper bound, in device-independent pixels.</summary>
        public double Maximum { get; set; } = double.PositiveInfinity;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // An unmeasured container reports 0 (and can report NaN/Infinity mid-layout): impose no
            // constraint rather than collapsing the bubble to the floor on the first pass.
            if (value is not double width || double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
                return double.PositiveInfinity;

            var scaled = width * Fraction;
            if (scaled < Minimum)
                scaled = Minimum;
            if (scaled > Maximum)
                scaled = Maximum;
            return scaled;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
