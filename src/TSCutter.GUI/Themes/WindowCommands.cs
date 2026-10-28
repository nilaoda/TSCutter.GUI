using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;

namespace TSCutter.GUI.Themes;

/// <summary>窗口操作统一经过 Closing 流程，保留取消和资源清理逻辑。</summary>
public static class WindowCommands
{
    private static readonly ConditionalWeakTable<Window, object> PendingCloses = new();

    public static IRelayCommand<Window> Close { get; } = new RelayCommand<Window>(window => window?.Close());

    /// <summary>关闭请求被运行中的任务拦截时，等待取消及清理完成后继续关闭。</summary>
    internal static void CloseWhenIdle(Window window, INotifyPropertyChanged source, Func<bool> isBusy)
    {
        if (PendingCloses.TryGetValue(window, out _))
            return;

        PendingCloses.Add(window, new object());
        source.PropertyChanged += OnPropertyChanged;
        window.Closed += OnClosed;
        window.DataContextChanged += OnClosed;
        OnPropertyChanged(source, new PropertyChangedEventArgs(null));

        void OnPropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (isBusy())
                return;

            Detach();
            // 等当前任务的 finally 和本次 Closing 事件结束后再发起关闭。
            Dispatcher.UIThread.Post(() =>
            {
                if (window.IsVisible && ReferenceEquals(window.DataContext, source))
                    window.Close();
            });
        }

        void OnClosed(object? sender, EventArgs args) => Detach();

        void Detach()
        {
            source.PropertyChanged -= OnPropertyChanged;
            window.Closed -= OnClosed;
            window.DataContextChanged -= OnClosed;
            PendingCloses.Remove(window);
        }
    }
}
