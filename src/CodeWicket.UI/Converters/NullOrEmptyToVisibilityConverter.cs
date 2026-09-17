using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CodeWicket.UI.Converters
{
    /// <summary>Collapses an element when the bound value is null or an empty string.</summary>
    public sealed class NullOrEmptyToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var empty = value is null || (value is string s && s.Length == 0);
            return empty ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
