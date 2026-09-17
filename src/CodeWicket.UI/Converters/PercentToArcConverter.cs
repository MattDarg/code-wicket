using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace CodeWicket.UI.Converters
{
    /// <summary>
    /// Turns a 0-100 percentage into the arc of a donut gauge — the filled portion of the context-usage
    /// ring in the chat's status strip, drawn over a full-circle track.
    /// <para>
    /// The arc starts at twelve o'clock and sweeps clockwise, which is what makes a ring readable as a
    /// gauge rather than a decoration. Geometry is produced for a box of <see cref="Diameter"/> on a
    /// side; the caller strokes it, so stroke thickness lives in XAML and never in this maths.
    /// </para>
    /// </summary>
    public sealed class PercentToArcConverter : IValueConverter
    {
        /// <summary>Width and height of the geometry's box. The arc is inscribed, so the stroke straddles it.</summary>
        public double Diameter { get; set; } = 12;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var percent = value switch
            {
                double d => d,
                int i => i,
                _ => 0d,
            };

            if (double.IsNaN(percent) || percent <= 0)
                return Geometry.Empty;

            var radius = Diameter / 2;
            var centre = new Point(radius, radius);

            // A full sweep can't be drawn as one ArcSegment — start and end coincide, and the arc
            // collapses to nothing rather than closing the circle. Hand it to an EllipseGeometry instead.
            if (percent >= 100)
                return new EllipseGeometry(centre, radius, radius);

            var angle = percent / 100 * 2 * Math.PI;
            var start = new Point(centre.X, centre.Y - radius);
            var end = new Point(
                centre.X + (radius * Math.Sin(angle)),
                centre.Y - (radius * Math.Cos(angle)));

            var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
            figure.Segments.Add(new ArcSegment
            {
                Point = end,
                Size = new Size(radius, radius),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = percent > 50,
            });

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Freeze(); // shared across every binding update; frozen so WPF can reuse it cheaply
            return geometry;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
