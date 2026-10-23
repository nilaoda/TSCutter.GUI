using System;
using Avalonia.Controls.ApplicationLifetimes;

namespace TSCutter.GUI.Services.Application;

/// <summary>先退出 AppKit 的终止回调，再由托管生命周期关闭应用。</summary>
internal sealed class MacOSShutdownHandler(Action<Action> post, Action tryShutdown)
{
    private bool _pending;
    private bool _retrying;

    public void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_retrying || e.Cancel)
            return;

        // Avalonia 11.3 会让 AppKit 直接调用 exit；NativeAOT 在原生调度器析构时可能因此中止。
        // 返回取消，让原生回调先退栈；下一轮仍用 TryShutdown，保留窗口取消关闭的机会。
        e.Cancel = true;
        if (_pending)
            return;
        _pending = true;
        post(() =>
        {
            _retrying = true;
            try
            {
                tryShutdown();
            }
            finally
            {
                _retrying = false;
                _pending = false;
            }
        });
    }
}
