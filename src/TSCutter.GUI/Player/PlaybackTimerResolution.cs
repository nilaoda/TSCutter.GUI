using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TSCutter.GUI.Player;

/// <summary>
/// Windows 默认定时器粒度约 15.6 ms，Task.Delay 和 GPU 同步等待都按它取整，播放时帧的释放时刻最多晚一个粒度。
/// 播放和测速期间把系统定时器精度提到 1 ms，最后一个使用者释放后恢复；其他平台不需要，也不做任何事。
/// 调用失败只记录日志，保持系统默认精度继续播放。
/// </summary>
internal static partial class PlaybackTimerResolution
{
    private const uint PeriodMilliseconds = 1;
    private static readonly Lock Sync = new();
    private static int users;
    private static bool active;

    /// <summary>申请 1 ms 定时器精度；释放返回的对象即归还，重复释放无效。</summary>
    public static IDisposable Acquire()
    {
        if (!OperatingSystem.IsWindows())
            return Lease.None;
        lock (Sync)
        {
            if (users++ == 0)
                active = TryBegin();
        }
        return new Lease();
    }

    private static void Release()
    {
        lock (Sync)
        {
            if (--users > 0 || !active)
                return;
            active = false;
            try { timeEndPeriod(PeriodMilliseconds); }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
            {
                // 申请成功时已加载过同一个库，这里只是保底。
            }
        }
    }

    private static bool TryBegin()
    {
        try
        {
            return timeBeginPeriod(PeriodMilliseconds) == 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            Console.WriteLine("High-resolution timer unavailable: " + exception.Message);
            return false;
        }
    }

    private sealed class Lease : IDisposable
    {
        internal static readonly IDisposable None = new NoLease();
        private int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
                Release();
        }
    }

    private sealed class NoLease : IDisposable
    {
        public void Dispose() { }
    }

    [LibraryImport("winmm.dll")] private static partial uint timeBeginPeriod(uint period);
    [LibraryImport("winmm.dll")] private static partial uint timeEndPeriod(uint period);
}
