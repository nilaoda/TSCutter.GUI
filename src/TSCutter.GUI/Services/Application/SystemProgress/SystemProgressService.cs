using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace TSCutter.GUI.Services.Application.SystemProgress;

/// <summary>
/// 把长时间操作的进度显示到任务栏按钮（Windows）或 Dock 图标（macOS）上。
/// 操作通过 Begin 登记，可在任意线程汇报；显示统一在界面线程定时刷新。
/// 主窗口显示全部操作的汇总；按窗口显示的平台上，独立的工具窗口另外显示它自己的操作。
/// 不支持的平台或系统调用出错时不显示，操作本身不受影响。
/// </summary>
internal static class SystemProgressService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(500);
    private static readonly Dictionary<Window, nint> ShownWindows = [];
    private static volatile bool _appActive = true;
    private static int _refreshQueued;
    private static IClassicDesktopStyleApplicationLifetime? _desktop;
    private static Func<bool>? _isEnabled;
    private static ISystemProgressIndicator? _indicator;
    private static DispatcherTimer? _timer;

    public static SystemProgressTracker Tracker { get; } = new(isAppActive: () => _appActive, showDelay: ShowDelay);

    /// <summary>登记一个长时间操作。owner 一般是操作所在窗口的视图模型。</summary>
    public static SystemProgressOperation Begin(object? owner = null) => Tracker.Begin(owner);

    /// <summary>在主窗口创建后调用一次。isEnabled 读取设置中的开关。</summary>
    public static void Initialize(IClassicDesktopStyleApplicationLifetime desktop, Func<bool> isEnabled)
    {
        if (_desktop != null)
            return;
        _indicator = CreateIndicator();
        if (_indicator == null)
            return;
        _desktop = desktop;
        _isEnabled = isEnabled;
        _timer = new DispatcherTimer(RefreshInterval, DispatcherPriority.Background, (_, _) => Refresh());
        Tracker.Changed += QueueRefresh;
        desktop.Exit += (_, _) => Shutdown();
        QueueRefresh();
    }

    private static ISystemProgressIndicator? CreateIndicator()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return WindowsTaskbarProgressIndicator.Create();
            if (OperatingSystem.IsMacOS())
                return new MacOSDockProgressIndicator();
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"System progress indicator is unavailable: {exception.Message}");
        }
        return null;
    }

    private static void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
            Dispatcher.UIThread.Post(() =>
            {
                Volatile.Write(ref _refreshQueued, 0);
                Refresh();
            }, DispatcherPriority.Background);
    }

    private static void Refresh()
    {
        if (_indicator is not { } indicator || _desktop is not { } desktop)
            return;

        UpdateAppActive(desktop);
        var enabled = _isEnabled?.Invoke() ?? true;
        try
        {
            var mainWindow = desktop.MainWindow ?? desktop.Windows.FirstOrDefault();
            var overall = enabled ? Tracker.GetSnapshot() : SystemProgressSnapshot.None;
            if (!indicator.TracksWindows)
            {
                indicator.Show(0, overall);
            }
            else
            {
                if (mainWindow != null)
                    ShowOnWindow(indicator, mainWindow, overall);
                foreach (var window in desktop.Windows)
                {
                    if (ReferenceEquals(window, mainWindow) || window.Owner != null || !window.ShowInTaskbar ||
                        window.DataContext is not { } owner)
                        continue;
                    ShowOnWindow(indicator, window, enabled ? Tracker.GetSnapshot(owner) : SystemProgressSnapshot.None);
                }
            }
        }
        catch (Exception exception)
        {
            // 系统调用出错后本次运行不再显示进度，避免反复出错。
            Trace.TraceWarning($"System progress indicator failed and is disabled: {exception}");
            Disable();
            return;
        }

        if (Tracker.IsIdle)
            _timer?.Stop();
        else if (_timer is { IsEnabled: false })
            _timer.Start();
    }

    private static void ShowOnWindow(ISystemProgressIndicator indicator, Window window, SystemProgressSnapshot snapshot)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? 0;
        if (handle == 0)
            return;
        if (ShownWindows.TryGetValue(window, out var previous))
        {
            if (previous != handle)
                indicator.Forget(previous);
        }
        else
        {
            // 窗口关闭时立即丢弃记录，不再持有已关闭的窗口。
            window.Closed += OnWindowClosed;
        }
        ShownWindows[window] = handle;
        indicator.Show(handle, snapshot);
    }

    private static void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not Window window)
            return;
        window.Closed -= OnWindowClosed;
        if (ShownWindows.Remove(window, out var handle))
            _indicator?.Forget(handle);
    }

    // 程序从后台切回前台时，视为用户已经看到之前的失败。
    private static void UpdateAppActive(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var active = desktop.Windows.Any(window => window.IsActive);
        var wasActive = _appActive;
        _appActive = active;
        if (active && !wasActive)
            Tracker.NotifyAppActivated();
    }

    private static void Disable()
    {
        _timer?.Stop();
        Tracker.Changed -= QueueRefresh;
        var indicator = _indicator;
        _indicator = null;
        foreach (var window in ShownWindows.Keys)
            window.Closed -= OnWindowClosed;
        ShownWindows.Clear();
        try
        {
            indicator?.Dispose();
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Failed to clear system progress indicator: {exception.Message}");
        }
    }

    private static void Shutdown() => Disable();
}
