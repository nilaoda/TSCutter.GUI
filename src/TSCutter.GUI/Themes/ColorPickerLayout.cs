using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace TSCutter.GUI.Themes;

/// <summary>允许选色器样式调整第三方模板中的固定列宽，不复制整套选色模板。</summary>
public sealed class ColorPickerLayout : AvaloniaObject
{
    public static readonly AttachedProperty<string?> ColumnsProperty =
        AvaloniaProperty.RegisterAttached<ColorPickerLayout, Grid, string?>("Columns");

    private static readonly AttachedProperty<GridLength[]?> OriginalColumnsProperty =
        AvaloniaProperty.RegisterAttached<ColorPickerLayout, Grid, GridLength[]?>("OriginalColumns");

    static ColorPickerLayout()
    {
        ColumnsProperty.Changed.AddClassHandler<Grid>((grid, change) =>
        {
            grid.Loaded -= OnLoaded;
            if (change.GetNewValue<string?>() is not null)
            {
                grid.Loaded += OnLoaded;
                if (grid.IsLoaded)
                    ApplyColumns(grid);
            }
            else if (grid.GetValue(OriginalColumnsProperty) is { } original)
            {
                SetWidths(grid, original);
                grid.ClearValue(OriginalColumnsProperty);
            }
        });
    }

    private static void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ApplyColumns((Grid)sender!);

    private static void ApplyColumns(Grid grid)
    {
        if (GetColumns(grid) is not { } columns)
            return;
        if (grid.GetValue(OriginalColumnsProperty) is null)
            grid.SetValue(OriginalColumnsProperty, grid.ColumnDefinitions.Select(column => column.Width).ToArray());
        SetWidths(grid, new ColumnDefinitions(columns).Select(column => column.Width).ToArray());
    }

    private static void SetWidths(Grid grid, GridLength[] widths)
    {
        if (grid.ColumnDefinitions.Count != widths.Length)
            return;
        // 修改现有定义会同时刷新 Grid 的单元格分类缓存，直接替换集合只会重新测量。
        for (var index = 0; index < widths.Length; index++)
            grid.ColumnDefinitions[index].Width = widths[index];
    }

    public static string? GetColumns(Grid grid) => grid.GetValue(ColumnsProperty);
    public static void SetColumns(Grid grid, string? value) => grid.SetValue(ColumnsProperty, value);
}
