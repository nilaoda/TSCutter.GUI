using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Rendering;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Classic.Avalonia.Theme;
using SkiaSharp;
using TSCutter.GUI.Themes;

namespace TSCutter.GUI.Web;

internal sealed partial class WebUiDesktop(IClassicDesktopStyleApplicationLifetime lifetime, Window mainWindow) : IDisposable
{
    private int Width = 1280;
    private int Height = 800;
    private PixelSize? resolutionLimit;
    private (PixelSize Output, PixelSize Viewport) recordedGeometry;
    private readonly HashSet<WebUiRecordedWindow> recordedWindows = [];
    private double browserPixelRatio = 1;
    private static readonly Point Outside = new(-1, -1);
    private Window? capturedWindow;
    private (Window Window, Vector Anchor)? windowDrag;
    private Window? hoveredWindow;
    private readonly HashSet<Key> pressedKeys = [];

    private WebUiSceneObserver? scene;
    internal long SceneVersion => (scene?.Version ?? 0) + WebUiRenderBackend.SceneVersion;
    private long lastInputTimestamp;
    private CaptureQuality quality = Sharp;
    private readonly record struct CaptureQuality(int Width, int Height, int JpegQuality, int Av1Bitrate);
    private static readonly CaptureQuality Smooth = new(960, 600, 62, 1_500_000);
    private static readonly CaptureQuality Balanced = new(1280, 800, 74, 3_000_000);
    private static readonly CaptureQuality Sharp = new(3840, 2160, 85, 7_000_000);

    public long LastInputTimestamp => Volatile.Read(ref lastInputTimestamp);
    public string Title => mainWindow.Title ?? string.Empty;

    public void Initialize()
    {
        scene = new WebUiSceneObserver(lifetime.Windows);
        StartInputWatchdog();
        popupPositionFix = Control.LoadedEvent.AddClassHandler<OverlayPopupHost>((popup, _) =>
        {
            // 无头弹层按窗口坐标定位，主题标题栏会使承载层原点下移。
            if (popup.GetVisualParent() is Visual layer && TopLevel.GetTopLevel(layer) is { } root &&
                layer.TranslatePoint(default, root) is { } origin)
                popup.RenderTransform = new Avalonia.Media.TranslateTransform(-origin.X, -origin.Y);
        });
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.CanMaximize = false;
        mainWindow.CanMinimize = false;
        mainWindow.Position = new PixelPoint(0, 0);
        mainWindow.Width = Width;
        mainWindow.Height = Height;
    }

    private Window[] GetWindows()
    {
        var windows = lifetime.Windows.Where(window => window.IsVisible).ToArray();
        Window.SortWindowsByZOrder(windows);
        return windows;
    }

    private Point Offset(Window window)
    {
        if (window == mainWindow)
            return default;
        return ClampWindowOffset(window, new Point(window.Position.X - mainWindow.Position.X,
            window.Position.Y - mainWindow.Position.Y));
    }

    private Point ClampWindowOffset(Window window, Point offset) =>
        new(Math.Clamp(offset.X, 0, Math.Max(0, Width - window.Bounds.Width)),
            Math.Clamp(offset.Y, 0, Math.Max(0, Height - window.Bounds.Height)));

    private static bool IsWindowDragArea(ClassicWindow window, Point point)
    {
        if (window.WindowState != WindowState.Normal)
            return false;

        if (window.InputHitTest(point) is Visual hit &&
            hit.GetSelfAndVisualAncestors().Any(visual => visual is OverlayPopupHost or LightDismissOverlayLayer))
            return false;
        if (DesktopWindowMenu.IsCaptionDragArea(window, point))
            return true;

        return window.Classes.Contains("macos-native-titlebar") &&
            window.GetVisualDescendants().OfType<Border>().Any(header =>
                header.Name == "MacOSHeader" && header.TemplatedParent == window && header.IsEffectivelyVisible &&
                header.TranslatePoint(default, window) is { } origin &&
                new Rect(origin, header.Bounds.Size).Contains(point));
    }

    private bool HandleWindowDrag(string kind, Point point, MouseButton button, RawInputModifiers modifiers, Window[] windows)
    {
        if (windowDrag is not { } drag)
            return false;
        if (!windows.Contains(drag.Window) || !CanReceiveInput(drag.Window, windows))
        {
            windowDrag = null;
            return false;
        }

        var released = kind == "up" && button == MouseButton.Left;
        var moving = kind == "move";
        var held = (modifiers & RawInputModifiers.LeftMouseButton) != 0;
        if (released || moving && held)
        {
            var offset = ClampWindowOffset(drag.Window, point - drag.Anchor);
            drag.Window.Position = mainWindow.Position + new PixelVector((int)offset.X, (int)offset.Y);
        }
        if (released || moving && !held)
            windowDrag = null;
        return true;
    }

    public WebUiCapturedFrame Capture(PixelSize? videoLimit, bool reset = false)
    {
        RefreshCursor();
        using var layout = scene?.PauseLayoutNotifications();
        var viewport = new PixelSize(Width, Height);
        var windows = GetWindows();

        // 输出像素尺寸独立于 Avalonia 的逻辑工作区。点击和子窗口位置仍使用逻辑坐标。
        var limit = resolutionLimit ?? new PixelSize(quality.Width, quality.Height);
        if (Height > Width)
            limit = new PixelSize(limit.Height, limit.Width);
        if (videoLimit is { } maximum)
        {
            if (Height > Width)
                maximum = new PixelSize(maximum.Height, maximum.Width);
            limit = new PixelSize(Math.Min(limit.Width, maximum.Width), Math.Min(limit.Height, maximum.Height));
        }
        // 分辨率只控制像素上限，不改变工作区比例；自动模式匹配浏览器实际显示密度。
        var scale = Math.Min((double)limit.Width / Width, (double)limit.Height / Height);
        if (resolutionLimit is null)
            scale = Math.Min(browserPixelRatio, scale);
        var size = new PixelSize(Math.Max(2, (int)Math.Round(Width * scale) & ~1),
            Math.Max(2, (int)Math.Round(Height * scale) & ~1));
        var scaleX = (double)size.Width / Width;
        var scaleY = (double)size.Height / Height;
        return CaptureRecorded(windows, size, viewport, scaleX, scaleY, reset);
    }

    private WebUiCapturedFrame CaptureRecorded(Window[] windows, PixelSize size, PixelSize viewport, double scaleX, double scaleY, bool reset)
    {
        var changedGeometry = reset || recordedGeometry != (size, viewport);
        var visible = windows.Select(WebUiRenderBackend.GetWindow).ToArray();
        // 隐藏窗口的像素缓存会被回收，再次显示时必须重新补齐完整画面。
        foreach (var previous in recordedWindows)
            if (!visible.Contains(previous))
                previous.Reset();
        recordedWindows.Clear();
        recordedWindows.UnionWith(visible);
        var refresh = false;
        foreach (var window in windows)
        {
            var recorded = WebUiRenderBackend.GetWindow(window);
            if (!changedGeometry && !recorded.NeedsRefresh)
                continue;
            recorded.BeginRefresh();
            // 整窗失效会覆盖所有脏区；缩放或丢弃增量后必须先补齐完整画面。
            window.InvalidateVisual();
            foreach (var visual in window.GetVisualDescendants())
                visual.InvalidateVisual();
            refresh = true;
        }
        if (refresh)
            foreach (var window in windows)
                ((IRenderRoot)window).Renderer.Paint(new Rect(window.Bounds.Size));
        recordedGeometry = (size, viewport);
        var layers = new List<WebUiWindowFrame>();
        try
        {
            foreach (var window in windows)
            {
                var bounds = new Rect(Offset(window), window.Bounds.Size).Intersect(new Rect(0, 0, Width, Height));
                if (bounds.Width <= 0 || bounds.Height <= 0)
                    continue;
                var recorded = WebUiRenderBackend.GetWindow(window);
                var pictures = recorded.TakePictures(out var damage);
                layers.Add(new WebUiWindowFrame(recorded, pictures,
                    new PixelSize(Math.Max(1, (int)Math.Ceiling(bounds.Width * scaleX)), Math.Max(1, (int)Math.Ceiling(bounds.Height * scaleY))),
                    new SKPoint((float)(bounds.X * scaleX), (float)(bounds.Y * scaleY)), new SKPoint((float)scaleX, (float)scaleY), damage));
            }
            var bitrateScale = Math.Max(1, Math.Sqrt(size.Width * (double)size.Height / (1920 * 1080)));
            return new WebUiCapturedFrame(layers, size, viewport,
                quality.JpegQuality, (int)(quality.Av1Bitrate * bitrateScale));
        }
        catch
        {
            foreach (var layer in layers)
                foreach (var picture in layer.Pictures)
                    picture.Dispose();
            throw;
        }
    }

    public void HandleInput(JsonElement input)
    {
        try
        {
            HandleInputCore(input);
        }
        finally { RefreshCursor(); }
    }

    private void HandleInputCore(JsonElement input)
    {
        var kind = String(input, "type");
        if (kind == "input-state")
        {
            SynchronizeInput(input);
            return;
        }
        lastHeldInputAt = Stopwatch.GetTimestamp();
        Volatile.Write(ref lastInputTimestamp, lastHeldInputAt);
        if (kind != "reset")
            scene?.Invalidate();
        if (kind == "resize")
        {
            resolutionLimit = String(input, "resolution") switch
            {
                "720" => new PixelSize(1280, 720),
                "1080" => new PixelSize(1920, 1080),
                "1440" => new PixelSize(2560, 1440),
                "2160" => new PixelSize(3840, 2160),
                _ => (PixelSize?)null
            };
            var ratio = Number(input, "pixelRatio");
            browserPixelRatio = double.IsFinite(ratio) && ratio > 0 ? Math.Clamp(ratio, 0.5, 4) : 1;
            var width = (int)Math.Clamp(Number(input, "width"), 670, 3840);
            var height = (int)Math.Clamp(Number(input, "height"), 400, 2160);
            if (String(input, "aspectRatio") == "16:9")
            {
                var units = Math.Clamp(Math.Min(width / 16, height / 9), 45, 240);
                width = units * 16;
                height = units * 9;
            }
            if (width == Width && height == Height)
                return;
            Width = width;
            Height = height;
            mainWindow.Width = width;
            mainWindow.Height = height;
            return;
        }
        if (kind == "quality")
        {
            quality = String(input, "quality") switch
            {
                "smooth" => Smooth,
                "balanced" => Balanced,
                "sharp" => Sharp,
                _ => quality
            };
            return;
        }
        if (kind == "locale")
        {
            var locale = String(input, "locale");
            if (WebUiSession.IsSupportedLocale(locale))
                App.ConfigService?.ApplyLanguage(locale!);
            return;
        }
        if (kind == "reset")
        {
            ResetInput();
            return;
        }

        var windows = GetWindows();
        if (windows.Length == 0)
            return;
        var activeWindow = windows.LastOrDefault(window => window.IsActive && CanReceiveInput(window, windows))
            ?? windows.LastOrDefault(window => CanReceiveInput(window, windows));
        if (activeWindow is null)
            return;
        var modifiers = ReadModifiers(input);
        if (kind is "keyDown" or "keyUp")
        {
            HandleKey(activeWindow, input, kind == "keyDown", modifiers);
            return;
        }
        if (kind == "text")
        {
            if (String(input, "text") is { Length: > 0 and <= 512 } value)
                activeWindow.KeyTextInput(value);
            return;
        }
        if (kind == "leave")
        {
            if (capturedWindow is null && windowDrag is null)
            {
                pointerPosition = null;
                ClearHover();
            }
            return;
        }
        if (kind is not ("down" or "up" or "move" or "wheel"))
            return;

        var point = new Point(Math.Clamp(Number(input, "x"), 0, Width), Math.Clamp(Number(input, "y"), 0, Height));
        pointerPosition = point;
        var button = Number(input, "button") switch
        {
            1 => MouseButton.Middle,
            2 => MouseButton.Right,
            _ => MouseButton.Left
        };
        var mask = button == MouseButton.Middle ? 4 : button == MouseButton.Right ? 2 : 1;
        if (kind == "down")
            pressedButtons |= mask;
        else if (kind == "up")
            pressedButtons &= ~mask;
        else if (kind == "move" && input.TryGetProperty("buttons", out var held) && held.ValueKind == JsonValueKind.Number &&
            held.TryGetInt32(out var actual) && actual is >= 0 and <= 7)
            ReleaseMissingButtons(actual);
        if (HandleWindowDrag(kind, point, button, modifiers, windows))
            return;
        if (capturedWindow is not null && (!windows.Contains(capturedWindow) || !CanReceiveInput(capturedWindow, windows)))
            capturedWindow = null;
        var target = capturedWindow ?? windows.LastOrDefault(window => new Rect(Offset(window), window.Bounds.Size).Contains(point));
        if (kind == "move" && !ReferenceEquals(hoveredWindow, target))
        {
            ClearHover();
            hoveredWindow = target;
        }
        if (target is null || !windows.Contains(target))
            return;
        if (!CanReceiveInput(target, windows))
            return;

        var offset = Offset(target);
        var localPointer = new Point(point.X - offset.X, point.Y - offset.Y);
        switch (kind)
        {
            case "down":
                if (!target.IsActive)
                    target.Activate();
                // 无头窗口没有原生拖动，使用浏览器指针在合成画面内移动子窗口。
                if (button == MouseButton.Left && target != mainWindow &&
                    target is ClassicWindow classic && IsWindowDragArea(classic, localPointer))
                {
                    ClearHover();
                    windowDrag = (target, point - offset);
                    break;
                }
                capturedWindow = target;
                target.MouseDown(localPointer, button, modifiers);
                break;
            case "up":
                capturedWindow = pressedButtons == 0 ? null : target;
                target.MouseUp(localPointer, button, modifiers);
                break;
            case "wheel":
                var delta = new Vector(Math.Clamp(Number(input, "dx"), -8, 8), Math.Clamp(Number(input, "dy"), -8, 8));
                target.MouseWheel(localPointer, delta, modifiers);
                break;
            default:
                target.MouseMove(localPointer, modifiers);
                break;
        }
    }

    private static bool CanReceiveInput(Window window, Window[] windows) => window.IsEnabled &&
        !windows.Any(child => child.IsDialog && child.Owner == window);

    private void ClearHover()
    {
        if (hoveredWindow is { IsVisible: true } window)
            window.MouseMove(Outside);
        hoveredWindow = null;
    }

    public void ResetInput()
    {
        pointerPosition = null;
        windowDrag = null;
        if (capturedWindow is { IsVisible: true } window)
        {
            window.MouseUp(Outside, MouseButton.Left);
            window.MouseUp(Outside, MouseButton.Middle);
            window.MouseUp(Outside, MouseButton.Right);
        }
        capturedWindow = null;
        ClearHover();
        var activeWindow = GetWindows().LastOrDefault();
        if (activeWindow is not null)
        {
            foreach (var key in pressedKeys)
                activeWindow.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        }
        pressedKeys.Clear();
        heldCodes.Clear();
        pressedButtons = 0;
        RefreshCursor();
        scene?.Invalidate();
    }

    private void HandleKey(Window window, JsonElement input, bool down, RawInputModifiers modifiers)
    {
        var symbol = String(input, "key");
        var code = String(input, "code");
        var name = symbol switch
        {
            " " => nameof(Key.Space),
            "Backspace" => nameof(Key.Back),
            "," => nameof(Key.OemComma),
            "." => nameof(Key.OemPeriod),
            "[" => nameof(Key.OemOpenBrackets),
            "]" => nameof(Key.OemCloseBrackets),
            "Control" => code == "ControlRight" ? nameof(Key.RightCtrl) : nameof(Key.LeftCtrl),
            "Shift" => code == "ShiftRight" ? nameof(Key.RightShift) : nameof(Key.LeftShift),
            "Alt" => code == "AltRight" ? nameof(Key.RightAlt) : nameof(Key.LeftAlt),
            "Meta" => code == "MetaRight" ? nameof(Key.RWin) : nameof(Key.LWin),
            { Length: 1 } when char.IsAsciiDigit(symbol[0]) => "D" + symbol,
            { } when symbol.StartsWith("Arrow", StringComparison.Ordinal) => symbol[5..],
            _ => symbol
        };
        if (!Enum.TryParse<Key>(name, ignoreCase: true, out var key) || !Enum.IsDefined(key))
            key = Key.None;
        var physicalName = code switch
        {
            { Length: 4 } when code.StartsWith("Key", StringComparison.Ordinal) => code[3..],
            _ => code
        };
        Enum.TryParse(physicalName, ignoreCase: true, out PhysicalKey physicalKey);
        if (down)
        {
            if (key != Key.None)
            {
                pressedKeys.Add(key);
                if (code is { Length: > 0 and <= 64 } && heldCodes.Count < 32)
                    heldCodes[code] = key;
            }
            window.KeyPress(key, modifiers, physicalKey, symbol);
        }
        else
        {
            if (code is not null && heldCodes.Remove(code, out var heldKey))
                key = heldKey;
            pressedKeys.Remove(key);
            window.KeyRelease(key, modifiers, physicalKey, symbol);
        }
    }

    private static RawInputModifiers ReadModifiers(JsonElement input)
    {
        var value = RawInputModifiers.None;
        if (Boolean(input, "shift"))
            value |= RawInputModifiers.Shift;
        if (Boolean(input, "control"))
            value |= RawInputModifiers.Control;
        if (Boolean(input, "alt"))
            value |= RawInputModifiers.Alt;
        if (Boolean(input, "meta"))
            value |= RawInputModifiers.Meta;
        var buttons = (int)Math.Clamp(Number(input, "buttons"), 0, 7);
        if ((buttons & 1) != 0)
            value |= RawInputModifiers.LeftMouseButton;
        if ((buttons & 2) != 0)
            value |= RawInputModifiers.RightMouseButton;
        if ((buttons & 4) != 0)
            value |= RawInputModifiers.MiddleMouseButton;
        return value;
    }

    private static string? String(JsonElement item, string name) =>
        item.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static bool Boolean(JsonElement item, string name) =>
        item.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;

    private static double Number(JsonElement item, string name) =>
        item.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
        property.TryGetDouble(out var result) && double.IsFinite(result) ? result : 0;
}
