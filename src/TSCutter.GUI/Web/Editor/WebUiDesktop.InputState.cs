using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Headless;
using Avalonia.Threading;

namespace TSCutter.GUI.Web;

internal sealed partial class WebUiDesktop
{
    private readonly Dictionary<string, Key> heldCodes = new(StringComparer.Ordinal);
    private DispatcherTimer? inputWatchdog;
    private IDisposable? popupPositionFix;
    private long lastHeldInputAt;
    private int pressedButtons;

    private void StartInputWatchdog()
    {
        inputWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        inputWatchdog.Tick += (_, _) => ExpireInput(Stopwatch.GetTimestamp());
        inputWatchdog.Start();
    }

    internal void ExpireInput(long now)
    {
        if ((pressedKeys.Count != 0 || pressedButtons != 0 || capturedWindow is not null || windowDrag is not null) &&
            Stopwatch.GetElapsedTime(lastHeldInputAt, now) >= TimeSpan.FromSeconds(3))
            ResetInput();
    }

    private void SynchronizeInput(JsonElement input)
    {
        if (!input.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array || keys.GetArrayLength() > 32 ||
            !input.TryGetProperty("buttons", out var buttons) || buttons.ValueKind != JsonValueKind.Number ||
            !buttons.TryGetInt32(out var mask) || mask is < 0 or > 7)
            return;
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys.EnumerateArray())
        {
            if (key.ValueKind != JsonValueKind.String || key.GetString() is not { Length: > 0 and <= 64 } code)
                return;
            actual.Add(code);
        }
        lastHeldInputAt = Stopwatch.GetTimestamp();
        // 只释放浏览器已经松开的键，不补造按下事件，避免重复触发快捷键。
        var windows = GetWindows();
        var target = windows.LastOrDefault(window => window.IsActive && CanReceiveInput(window, windows))
            ?? windows.LastOrDefault(window => CanReceiveInput(window, windows));
        foreach (var code in heldCodes.Keys.Where(code => !actual.Contains(code)).ToArray())
        {
            var released = heldCodes[code];
            heldCodes.Remove(code);
            if (heldCodes.ContainsValue(released))
                continue;
            pressedKeys.Remove(released);
            scene?.Invalidate();
            target?.KeyRelease(released, RawInputModifiers.None, PhysicalKey.None, null);
        }
        ReleaseMissingButtons(mask);
    }

    private void ReleaseMissingButtons(int actual)
    {
        var missing = pressedButtons & ~actual;
        if (missing == 0)
            return;
        scene?.Invalidate();
        if ((missing & 1) != 0)
            windowDrag = null;
        if (capturedWindow is { IsVisible: true } window)
        {
            if ((missing & 1) != 0)
                window.MouseUp(Outside, MouseButton.Left);
            if ((missing & 2) != 0)
                window.MouseUp(Outside, MouseButton.Right);
            if ((missing & 4) != 0)
                window.MouseUp(Outside, MouseButton.Middle);
        }
        pressedButtons &= actual;
        if (pressedButtons == 0)
            capturedWindow = null;
    }

    public void Dispose()
    {
        inputWatchdog?.Stop();
        popupPositionFix?.Dispose();
        scene?.Dispose();
        foreach (var window in recordedWindows)
            window.Reset();
        recordedWindows.Clear();
        ResetInput();
    }
}
