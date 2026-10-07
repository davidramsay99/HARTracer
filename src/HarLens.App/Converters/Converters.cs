using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace HarLens.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value switch
        {
            bool b => b,
            int i => i != 0,
            string s => s.Length > 0,
            null => false,
            _ => true,
        };
        return flag != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility v && (v == Visibility.Visible) != Invert;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is not null && value is not string { Length: 0 }) != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Maps an annotation color name ("red", "blue", ...) to the themed tint brush.</summary>
public sealed class ColorMarkBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string { Length: > 0 } name)
        {
            return Brushes.Transparent;
        }

        var key = "Mark" + char.ToUpperInvariant(name[0]) + name[1..];
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Background for diff rows by kind.</summary>
public sealed class DiffKindBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value?.ToString() switch
        {
            "Inserted" => "DiffInsertedBackground",
            "Deleted" => "DiffDeletedBackground",
            "Modified" => "DiffModifiedBackground",
            _ => null,
        };
        return key is null ? Brushes.Transparent : Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class EqualityToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null)
        {
            return Binding.DoNothing;
        }

        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return underlying.IsEnum ? Enum.Parse(underlying, parameter.ToString()!) : parameter;
    }
}

/// <summary>Looks a brush up by resource key (for theme-aware colors chosen in data, such as waterfall phases).</summary>
public sealed class ResourceBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key ? Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray : Brushes.Gray;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible when the value's text equals the parameter (one of several, separated by '|').</summary>
public sealed class EqualityToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value?.ToString();
        var options = parameter?.ToString()?.Split('|') ?? [];
        return options.Contains(text, StringComparer.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
