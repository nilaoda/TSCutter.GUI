using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace TSCutter.GUI.FFmpeg;

/// <summary>收集当前线程同步原生操作的错误日志；作用域不得跨越 await。</summary>
internal sealed unsafe class FFmpegLogCapture : IDisposable
{
    private const int ErrorLevel = 16;
    private const int MaximumLength = 8192;
    [ThreadStatic] private static FFmpegLogCapture? current;
    private readonly FFmpegLogCapture? previous;
    private readonly StringBuilder messages = new();

    // 回调只安装一次；其他线程及非错误日志继续交由 FFmpeg 的默认回调处理。
    static FFmpegLogCapture() => NativeMethods.av_log_set_callback(&OnLog);

    public FFmpegLogCapture()
    {
        previous = current;
        current = this;
    }

    public string Messages => messages.ToString().Trim();
    public void Clear() => messages.Clear();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLog(void* context, int level, byte* format, byte* arguments)
    {
        try
        {
            if (current is not { } capture || (level & 255) > ErrorLevel)
            {
                NativeMethods.av_log_default_callback(context, level, format, arguments);
                return;
            }
            byte* buffer = stackalloc byte[4096];
            var prefix = 1;
            NativeMethods.av_log_format_line2(context, level, format, arguments, buffer, 4096, &prefix);
            var message = Marshal.PtrToStringUTF8((nint)buffer) ?? "";
            capture.messages.Append(message.AsSpan(0, Math.Min(message.Length, MaximumLength - capture.messages.Length)));
            // va_list 只能消费一次，格式化后直接输出文本，不能再调用默认回调复用参数。
            Console.Error.Write(message);
        }
        catch
        {
            // 日志错误不能穿过原生回调边界，也不能改变媒体操作的成功与失败。
        }
    }

    public void Dispose() => current = previous;
}
