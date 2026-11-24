using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Classic.Avalonia.Theme;

namespace TSCutter.GUI.Web;

internal sealed partial class WebUiDesktop
{
    private Point? pointerPosition;
    private Window? cursorWindow;
    private InputElement? cursorElement;
    internal string CursorStyle { get; private set; } = "default";
    internal event Action<string>? CursorChanged;

    private void RefreshCursor()
    {
        var windows = GetWindows();
        var window = pointerPosition is { } position
            ? windows.LastOrDefault(candidate => new Rect(Offset(candidate), candidate.Bounds.Size).Contains(position))
            : null;
        if (capturedWindow is { IsVisible: true })
            window = capturedWindow;
        if (windowDrag is { } drag && windows.Contains(drag.Window))
            window = drag.Window;
        if (window is not null && !CanReceiveInput(window, windows))
            window = null;
        if (!ReferenceEquals(cursorWindow, window))
        {
            if (cursorWindow is not null)
                cursorWindow.PropertyChanged -= CursorWindowChanged;
            cursorWindow = window;
            if (cursorWindow is not null)
                cursorWindow.PropertyChanged += CursorWindowChanged;
        }
        // 读取继承后的光标，保留控件悬停和拖动过程中动态切换的样式。
        var element = window?.GetValue(TopLevel.PointerOverElementProperty) as InputElement;
        if (!ReferenceEquals(cursorElement, element))
        {
            if (cursorElement is not null)
                cursorElement.PropertyChanged -= CursorElementChanged;
            cursorElement = element;
            if (cursorElement is not null)
                cursorElement.PropertyChanged += CursorElementChanged;
        }
        var style = ToCssCursor(element?.Cursor);
        if (window is not null && pointerPosition is { } point)
        {
            if (windowDrag is not null)
                style = "grabbing";
            else if (window != mainWindow && window is ClassicWindow classic)
            {
                var offset = Offset(window);
                if (IsWindowDragArea(classic, new Point(point.X - offset.X, point.Y - offset.Y)))
                    style = "grab";
            }
        }
        if (style == CursorStyle)
            return;
        CursorStyle = style;
        CursorChanged?.Invoke(style);
    }

    private void CursorWindowChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TopLevel.PointerOverElementProperty || e.Property == Visual.IsVisibleProperty)
            RefreshCursor();
    }

    private void CursorElementChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == InputElement.CursorProperty)
            RefreshCursor();
    }

    private static string ToCssCursor(Cursor? cursor) => cursor?.ToString() switch
    {
        "Ibeam" => "text",
        "Wait" => "wait",
        "Cross" => "crosshair",
        "SizeWestEast" => "ew-resize",
        "SizeNorthSouth" => "ns-resize",
        "SizeAll" or "DragMove" => "move",
        "No" => "not-allowed",
        "Hand" => "pointer",
        "AppStarting" => "progress",
        "Help" => "help",
        "TopSide" => "n-resize",
        "BottomSide" => "s-resize",
        "LeftSide" => "w-resize",
        "RightSide" => "e-resize",
        "TopLeftCorner" or "BottomRightCorner" => "nwse-resize",
        "TopRightCorner" or "BottomLeftCorner" => "nesw-resize",
        "DragCopy" => "copy",
        "DragLink" => "alias",
        "None" => "none",
        _ => "default"
    };
}
