using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace PhotoEditor.Converters;

/// <summary>Rating → "★" for star number ConverterParameter and below, "☆" above it.</summary>
public sealed class StarConverter : IValueConverter
{
    public static readonly StarConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int rating && int.TryParse(parameter?.ToString(), out int star) && rating >= star ? "★" : "☆";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>true → dimmed (0.35), false → opaque.</summary>
public sealed class BoolToOpacity : IValueConverter
{
    public static readonly BoolToOpacity Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 0.35 : 1.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
