using System;

namespace TSCutter.GUI.Services.Application.SystemProgress;

/// <summary>平台的进度显示。只在界面线程调用。</summary>
internal interface ISystemProgressIndicator : IDisposable
{
    /// <summary>为 true 时按窗口句柄分别显示；为 false 时整个程序只有一处显示，忽略句柄。</summary>
    bool TracksWindows { get; }

    void Show(nint windowHandle, SystemProgressSnapshot snapshot);

    /// <summary>窗口关闭后丢弃它的记录。</summary>
    void Forget(nint windowHandle);
}
