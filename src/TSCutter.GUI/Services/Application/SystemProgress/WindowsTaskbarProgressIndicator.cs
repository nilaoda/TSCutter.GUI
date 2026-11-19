using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace TSCutter.GUI.Services.Application.SystemProgress;

/// <summary>通过 ITaskbarList3 在窗口的任务栏按钮上显示进度。只能在界面线程调用。</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsTaskbarProgressIndicator : ISystemProgressIndicator
{
    private const uint ClsctxInprocServer = 0x1;
    private const ulong ProgressTotal = 10000;
    private static readonly Guid TaskbarListClsid = new("56FDF344-FD6D-11d0-958A-006097C9A090");
    private static readonly Guid TaskbarList3Iid = new("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF");

    private readonly ITaskbarList3 _taskbar;
    private readonly Dictionary<nint, SystemProgressSnapshot> _shown = [];

    private WindowsTaskbarProgressIndicator(ITaskbarList3 taskbar) => _taskbar = taskbar;

    public bool TracksWindows => true;

    /// <summary>创建任务栏接口；系统不提供时抛出异常。</summary>
    public static WindowsTaskbarProgressIndicator Create()
    {
        var hr = CoCreateInstance(TaskbarListClsid, 0, ClsctxInprocServer, TaskbarList3Iid, out var pointer);
        if (hr < 0 || pointer == 0)
            throw new COMException("CoCreateInstance(TaskbarList) failed.", hr);
        try
        {
            var taskbar = (ITaskbarList3)new StrategyBasedComWrappers()
                .GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
            hr = taskbar.HrInit();
            if (hr < 0)
                throw new COMException("ITaskbarList3.HrInit failed.", hr);
            return new WindowsTaskbarProgressIndicator(taskbar);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    public void Show(nint windowHandle, SystemProgressSnapshot snapshot)
    {
        if (windowHandle == 0)
            return;
        if (_shown.TryGetValue(windowHandle, out var previous) && previous.LooksSameAs(snapshot))
            return;
        if (snapshot.State == SystemProgressState.None && !_shown.ContainsKey(windowHandle))
            return;

        // 先切换状态再设置进度值：错误和暂停状态下设置进度值不会把状态改回正常。
        // 窗口已关闭等情况下调用会返回失败，不影响其他窗口，忽略即可。
        _ = _taskbar.SetProgressState(windowHandle, ToFlags(snapshot.State));
        if (snapshot.State is SystemProgressState.Normal or SystemProgressState.Error or SystemProgressState.Paused)
            _ = _taskbar.SetProgressValue(windowHandle, (ulong)Math.Round(Math.Clamp(snapshot.Value, 0, 1) * ProgressTotal),
                ProgressTotal);

        if (snapshot.State == SystemProgressState.None)
            _shown.Remove(windowHandle);
        else
            _shown[windowHandle] = snapshot;
    }

    public void Forget(nint windowHandle) => _shown.Remove(windowHandle);

    public void Dispose()
    {
        foreach (var handle in _shown.Keys)
            _ = _taskbar.SetProgressState(handle, TaskbarProgressFlags.NoProgress);
        _shown.Clear();
        if (_taskbar is IDisposable disposable)
            disposable.Dispose();
    }

    private static TaskbarProgressFlags ToFlags(SystemProgressState state) => state switch
    {
        SystemProgressState.Indeterminate => TaskbarProgressFlags.Indeterminate,
        SystemProgressState.Normal => TaskbarProgressFlags.Normal,
        SystemProgressState.Error => TaskbarProgressFlags.Error,
        SystemProgressState.Paused => TaskbarProgressFlags.Paused,
        _ => TaskbarProgressFlags.NoProgress,
    };

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    internal enum TaskbarProgressFlags
    {
        NoProgress = 0,
        Indeterminate = 0x1,
        Normal = 0x2,
        Error = 0x4,
        Paused = 0x8,
    }

    // 按虚表顺序声明 ITaskbarList、ITaskbarList2 和 ITaskbarList3 的方法，只用到前面这一段。
    [GeneratedComInterface]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
    internal partial interface ITaskbarList3
    {
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(nint hwnd);
        [PreserveSig] int DeleteTab(nint hwnd);
        [PreserveSig] int ActivateTab(nint hwnd);
        [PreserveSig] int SetActiveAlt(nint hwnd);
        [PreserveSig] int MarkFullscreenWindow(nint hwnd, int fullscreen);
        [PreserveSig] int SetProgressValue(nint hwnd, ulong completed, ulong total);
        [PreserveSig] int SetProgressState(nint hwnd, TaskbarProgressFlags flags);
    }
}
