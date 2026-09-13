using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VideoDownloader.UI.Converters;

/// <summary>Maps null to Auto (NaN) width; otherwise the numeric width.</summary>
public sealed class NullableDoubleToWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d && d > 0 && !double.IsNaN(d) && !double.IsInfinity(d))
            return d;
        return double.NaN;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>MaxWidth is always ≤200; when FixedWidth is set (shrink mode) MaxWidth matches that share.</summary>
public sealed class TabMaxWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d && d > 0 && !double.IsNaN(d) && !double.IsInfinity(d))
            return Math.Min(200d, d);
        return 200d;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
