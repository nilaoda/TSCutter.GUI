using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace TSCutter.GUI.Converters;

public class EnumToBooleanConverter : IValueConverter
{
    public static readonly EnumToBooleanConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value?.Equals(parameter);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true)
        {
            return parameter;
        }
        return AvaloniaProperty.UnsetValue;
    }
}