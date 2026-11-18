using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;

namespace TSCutter.GUI.Utils.Application;

/// <summary>
/// 打开对话框前等待较久时，在当前活动窗口上显示忙碌指针并暂时禁用窗口内容，效果等同于对话框已经弹出，
/// 避免等待期间触发其他操作；释放时恢复原状。只在界面线程使用。
/// </summary>
internal static class BusyCursor
{
    // 整个程序共用一个忙碌指针，不随每次等待创建和释放。
    private static readonly Cursor WaitCursor = new(StandardCursorType.Wait);
    // 同一窗口可能同时有多处在等待：按次数计，最后一处结束时才恢复；恢复后即移除，不保留窗口引用。
    private static readonly Dictionary<Window, State> Active = [];

    private sealed class State(Cursor? previousCursor, InputElement? disabledContent)
    {
        public int Count { get; set; } = 1;
        public Cursor? PreviousCursor { get; } = previousCursor;
        public InputElement? DisabledContent { get; } = disabledContent;
    }

    public static IDisposable? ShowOnActiveWindow()
    {
        if (!Dispatcher.UIThread.CheckAccess() ||
            Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;
        var window = desktop.Windows.FirstOrDefault(item => item.IsActive) ?? desktop.MainWindow;
        return window is null ? null : Show(window);
    }

    internal static IDisposable Show(Window window)
    {
        if (Active.TryGetValue(window, out var state))
        {
            state.Count++;
        }
        else
        {
            // 只禁用原本可用的内容，恢复时也只恢复由这里禁用的部分。
            var content = window.Content as InputElement;
            if (content is { IsEnabled: false })
                content = null;
            Active[window] = new State(window.Cursor, content);
            window.Cursor = WaitCursor;
            if (content is not null)
                content.IsEnabled = false;
        }
        return new Release(window);
    }

    private sealed class Release(Window window) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed || !Active.TryGetValue(window, out var state))
                return;
            _disposed = true;
            if (--state.Count > 0)
                return;
            Active.Remove(window);
            // 等待期间别处改过指针或内容时不覆盖。
            if (ReferenceEquals(window.Cursor, WaitCursor))
                window.Cursor = state.PreviousCursor;
            if (state.DisabledContent is { IsEnabled: false } content && ReferenceEquals(window.Content, content))
                content.IsEnabled = true;
        }
    }
}
