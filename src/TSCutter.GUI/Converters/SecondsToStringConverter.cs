
using System;
using System.Globalization;
using Avalonia.Data.Converters;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Converters;
public class SecondsToStringConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double seconds)
        {
            return CommonUtil.FormatSeconds(seconds);
        }
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}