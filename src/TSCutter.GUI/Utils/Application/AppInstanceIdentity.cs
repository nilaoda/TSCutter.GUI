using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace TSCutter.GUI.Utils.Application;

/// <summary>
/// 一个程序实例的标识：进程号加进程启动时间（UTC 刻度）。
/// 进程号会被系统复用，只有两者都吻合才认为是同一个实例，用来区分多开实例的文件、判断上次运行是否仍在进行。
/// </summary>
internal readonly record struct AppInstanceIdentity(int ProcessId, long StartTimeUtcTicks)
{
    // 不同平台取到的启动时间精度不同，比较时允许一秒误差；进程号被复用时启动时间必然相差更多。
    private static readonly long Tolerance = TimeSpan.TicksPerSecond;

    public static AppInstanceIdentity Current { get; } = FromCurrentProcess();

    /// <summary>用于文件名的形式：“进程号-启动时间刻度”。</summary>
    public string FileKey => string.Create(CultureInfo.InvariantCulture, $"{ProcessId}-{StartTimeUtcTicks}");

    public static bool TryParseFileKey(string text, out AppInstanceIdentity identity)
    {
        identity = default;
        var separator = text.IndexOf('-');
        if (separator <= 0 ||
            !int.TryParse(text.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var processId) ||
            !long.TryParse(text.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
            processId <= 0 || ticks <= 0)
            return false;
        identity = new AppInstanceIdentity(processId, ticks);
        return true;
    }

    /// <summary>该实例是否仍在运行：进程号存在且启动时间吻合。读取不到进程信息时视为已不在运行。</summary>
    public static bool IsRunning(AppInstanceIdentity identity)
    {
        if (identity == Current)
            return true;
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (process.HasExited)
                return false;
            var started = process.StartTime.ToUniversalTime().Ticks;
            return Math.Abs(started - identity.StartTimeUtcTicks) <= Tolerance;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                              Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static AppInstanceIdentity FromCurrentProcess()
    {
        using var process = Process.GetCurrentProcess();
        long ticks;
        try
        {
            ticks = process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception exception) when (exception is InvalidOperationException or
                                              Win32Exception or NotSupportedException)
        {
            ticks = DateTime.UtcNow.Ticks;
        }
        return new AppInstanceIdentity(process.Id, ticks);
    }
}
