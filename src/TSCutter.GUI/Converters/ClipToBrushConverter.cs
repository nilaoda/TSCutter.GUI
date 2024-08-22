using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace TSCutter.GUI.Converters;

public class ClipToBrushConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values?.Count != 3 || !targetType.IsAssignableFrom(typeof(LinearGradientBrush)))
            throw new NotSupportedException();
        
        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        };

        if (values[0] is not double durationMax ||
            values[1] is not double startTime ||
            values[2] is not double endTime ||
            durationMax <= 0)
        {
            gradient.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            gradient.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            return gradient;
        }

        // scale to 0-1
        var highlightStart = startTime;
        var highlightEnd = endTime < 0 ? durationMax : endTime;
        var normalizedHighlightStart = highlightStart / durationMax;
        var normalizedHighlightEnd = highlightEnd / durationMax;

        // GradientStops
        gradient.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        gradient.GradientStops.Add(new GradientStop(Colors.Transparent, normalizedHighlightStart));
        gradient.GradientStops.Add(new GradientStop(Colors.Green, normalizedHighlightStart));
        gradient.GradientStops.Add(new GradientStop(Colors.Green, normalizedHighlightEnd));
        gradient.GradientStops.Add(new GradientStop(Colors.Transparent, normalizedHighlightEnd));
        gradient.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            
        return gradient;
    }
}