using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using LoLKarmaBook.Core;
using LoLKarmaBook.Desktop.ViewModels;

namespace LoLKarmaBook.Desktop.Converters;

/// <summary>true → Visible, false → Collapsed. Optional "Invert" parameter.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        if (parameter is string text && string.Equals(text, "Invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

/// <summary>Non-empty string → Visible.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool hasText = value is string s && !string.IsNullOrWhiteSpace(s);
        if (parameter is string text && string.Equals(text, "Invert", StringComparison.OrdinalIgnoreCase))
        {
            hasText = !hasText;
        }

        return hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isNull = value is null;
        if (parameter is string text && string.Equals(text, "Invert", StringComparison.OrdinalIgnoreCase))
        {
            isNull = !isNull;
        }

        return isNull ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>WPF-free chart points → the PointCollection a Polyline needs.</summary>
public sealed class ChartPointsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var points = new PointCollection();
        if (value is IEnumerable<ChartPlotPoint> rows)
        {
            foreach (ChartPlotPoint row in rows)
            {
                points.Add(new Point(row.X, row.Y));
            }
        }

        points.Freeze();
        return points;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Status severity → brush (success/warning/error colour of the theme).</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value switch
        {
            StatusSeverity.Error => "Brush.Danger",
            StatusSeverity.Warning => "Brush.Warning",
            StatusSeverity.Success => "Brush.Success",
            _ => "Brush.TextMuted"
        };

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>String severity ("info"/"warning"/"error"/"success") → brush.</summary>
public sealed class SeverityTextToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = (value as string) switch
        {
            "error" => "Brush.Danger",
            "warning" => "Brush.Warning",
            "success" => "Brush.Success",
            _ => "Brush.Accent"
        };

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Resolves an icon key ("Icon.AfkLeave") to its Geometry resource.</summary>
public sealed class IconKeyToGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key)
        {
            return null;
        }

        return Application.Current?.TryFindResource(key) as Geometry;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Count → Visible when 0 ("empty"), or when &gt; 0 ("NonEmpty").</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int count = value is int i ? i : 0;
        bool nonEmpty = parameter is string s && string.Equals(s, "NonEmpty", StringComparison.OrdinalIgnoreCase);
        return (nonEmpty ? count > 0 : count == 0) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Inverse boolean for IsEnabled bindings ("跑一次快捷转换").</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not bool b || !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not bool b || !b;
}

/// <summary>Border style selected by a boolean, used for active filters/rows.</summary>
public sealed class SelectedBorderBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool selected = value is bool b && b;
        string key = selected ? "Brush.Accent" : "Brush.Border";
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
