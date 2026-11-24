using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;

namespace TSCutter.GUI.Web;

internal sealed class WebUiSceneObserver : IDisposable
{
    private readonly Dictionary<Window, INotifyPropertyChanged?> windows = new();
    private readonly IDisposable opened;
    private readonly IDisposable closed;
    private long version;
    private bool capturing;

    internal long Version => Volatile.Read(ref version);

    internal WebUiSceneObserver(IEnumerable<Window> existing)
    {
        opened = Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => Observe(window));
        closed = Window.WindowClosedEvent.AddClassHandler<Window>((window, _) => Remove(window));
        foreach (var window in existing)
            Observe(window);
    }

    private void Observe(Window window)
    {
        if (windows.ContainsKey(window))
            return;
        windows.Add(window, null);
        window.LayoutUpdated += LayoutChanged;
        window.PositionChanged += WindowChanged;
        window.Activated += WindowChanged;
        window.DataContextChanged += ContextChanged;
        window.PropertyChanged += PropertyChanged;
        UpdateContext(window);
    }

    private void UpdateContext(Window window)
    {
        if (windows[window] is { } previous)
            previous.PropertyChanged -= ModelChanged;
        var current = window.DataContext as INotifyPropertyChanged;
        windows[window] = current;
        if (current is not null)
            current.PropertyChanged += ModelChanged;
        Invalidate();
    }

    private void Remove(Window window)
    {
        if (!windows.Remove(window, out var model))
            return;
        if (model is not null)
            model.PropertyChanged -= ModelChanged;
        window.LayoutUpdated -= LayoutChanged;
        window.PositionChanged -= WindowChanged;
        window.Activated -= WindowChanged;
        window.DataContextChanged -= ContextChanged;
        window.PropertyChanged -= PropertyChanged;
        Invalidate();
    }

    private void ContextChanged(object? sender, EventArgs args)
    {
        if (sender is Window window)
            UpdateContext(window);
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs args) => Invalidate();
    private void WindowChanged(object? sender, EventArgs args) => Invalidate();
    private void LayoutChanged(object? sender, EventArgs args)
    {
        if (!capturing)
            Invalidate();
    }
    private void PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == Visual.BoundsProperty || args.Property == Visual.IsVisibleProperty ||
            args.Property == Window.WindowStateProperty)
            Invalidate();
    }

    internal void Invalidate() => Interlocked.Increment(ref version);

    internal LayoutScope PauseLayoutNotifications()
    {
        // 主动离屏绘制也会执行布局，不能让这类通知反过来不停触发下一次采集。
        capturing = true;
        return new LayoutScope(this);
    }

    internal readonly struct LayoutScope(WebUiSceneObserver owner) : IDisposable
    {
        public void Dispose() => owner.capturing = false;
    }

    public void Dispose()
    {
        opened.Dispose();
        closed.Dispose();
        foreach (var window in windows.Keys.ToArray())
            Remove(window);
    }
}
