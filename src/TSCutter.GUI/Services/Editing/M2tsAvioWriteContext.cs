using System;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Services.TransportStream.Analysis;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Services.Editing;

/// <summary>
/// M2TS 导出专用的自定义 AVIO 写出上下文：封装器实际写出的每个字节交给导出检测
/// 会话；只有成功写入托管文件流的字节才参与检测；检测的是封装器生成的最终 M2TS 字节（含最后 flush 的内容），
/// 不是输入 AVPacket。只支持顺序写出——出现 seek/回写时停止喂入检测会话，
/// 绝不把覆盖写入当成追加数据，也不会退回导出后重读文件。
/// </summary>
internal sealed unsafe class M2tsAvioWriteContext : IDisposable
{
    private const int AvioBufferSize = 64 * 1024;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WriteCallback(nint opaque, byte* buffer, int size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long SeekCallback(nint opaque, long offset, int whence);

    private readonly Stream _fileStream;
    private Exception? _writeException;
    private readonly ExportTsCheckSession _session;
    private readonly WriteCallback _write;
    private readonly SeekCallback _seek;
    private AVIOContext* _io;
    private long _position;
    private bool _seekDetected;
    private bool _finished;

    private M2tsAvioWriteContext(Stream stream, ExportTsCheckSession session)
    {
        _fileStream = stream;
        _session = session;
        _write = Write;
        _seek = Seek;
    }

    /// <summary>创建写上下文；失败时抛出，内部资源已自行清理。</summary>
    public static M2tsAvioWriteContext Open(string temporaryPath, ExportTsCheckSession session) =>
        Open(new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None), session);

    // 上下文接管流的所有权；测试也通过此入口覆盖最后一次 flush 的写入失败。
    internal static M2tsAvioWriteContext Open(Stream stream, ExportTsCheckSession session)
    {
        var writer = new M2tsAvioWriteContext(stream, session);
        try
        {
            writer.Allocate();
            return writer;
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    public AVIOContext* Io => _io;

    private void Allocate()
    {
        byte* buffer = (byte*)FF.av_mallocz(AvioBufferSize);
        try
        {
            if (buffer == null)
                throw new OutOfMemoryException();
            var io = FF.avio_alloc_context(buffer, AvioBufferSize, 1, 0, 0,
                Marshal.GetFunctionPointerForDelegate(_write),
                Marshal.GetFunctionPointerForDelegate(_seek));
            if (io == null)
                throw new OutOfMemoryException();
            // AVIO 可能替换其内部缓冲区，释放时以 io->buffer 为准。
            buffer = null;
            io->seekable = 1;
            _io = io;
        }
        finally
        {
            if (buffer != null)
                FF.av_free(buffer);
        }
    }

    /// <summary>
    /// 导出成功收尾：把封装器缓冲区中剩余的 M2TS 字节经写回调交给检测会话，
    /// 释放原生上下文后关闭文件流。文件流与临时文件的替换顺序由调用方保持。
    /// </summary>
    public void Complete()
    {
        if (_finished || _io == null)
            return;
        _finished = true;
        try
        {
            FF.avio_flush(_io);
            // 原生回调只能返回错误码，avio_flush 本身不返回错误，必须检查 AVIO 的粘滞错误。
            if (_writeException is not null)
                throw new IOException("Failed to write the M2TS output.", _writeException);
            FFmpegException.Check(_io->error);
            _fileStream.Flush();
        }
        finally
        {
            try
            {
                FreeNative();
            }
            finally
            {
                _fileStream.Dispose();
                GC.KeepAlive(_write);
                GC.KeepAlive(_seek);
            }
        }
        // 检测会话由导出任务在文件关闭、最终替换成功后统一完成。
    }

    public void Dispose()
    {
        if (_finished)
            return;
        _finished = true;
        FreeNative();
        try
        {
            _fileStream.Dispose();
        }
        catch
        {
            // 临时文件清理失败不覆盖真正的导出错误。
        }
        GC.KeepAlive(_write);
        GC.KeepAlive(_seek);
    }

    private void FreeNative()
    {
        var io = _io;
        _io = null;
        if (io == null)
            return;
        FF.av_free(io->buffer);
        // 局部指针变量本身已被固定，直接取地址传给释放函数。
        FF.avio_context_free(&io);
    }

    private int Write(nint opaque, byte* buffer, int size)
    {
        try
        {
            if (size <= 0)
                return 0;
            var data = new ReadOnlySpan<byte>(buffer, size);
            // 文件写入错误以错误码交还封装器；检测器自身异常由会话隔离。
            _fileStream.Write(data);
            _session.Append(data);
            _position += size;
            return size;
        }
        catch (Exception exception)
        {
            _writeException ??= exception;
            // 托管异常不得穿过原生回调边界。
            return ffmpeg.AVERROR_EXTERNAL;
        }
    }

    private long Seek(nint opaque, long offset, int whence)
    {
        try
        {
            if ((whence & 0x10000) != 0) // AVSEEK_SIZE
                return _fileStream.Length;
            long target;
            switch (whence & ~0x20000) // AVSEEK_FORCE
            {
                case 0: // SEEK_SET
                    target = offset;
                    break;
                case 1: // SEEK_CUR
                    target = _position + offset;
                    break;
                case 2: // SEEK_END
                    target = _fileStream.Length + offset;
                    break;
                default:
                    return -22; // EINVAL
            }
            if (target < 0)
                return -22;
            if (target != _position && !_seekDetected)
            {
                // M2TS 顺序写出被回写打断：文件内容仍要保证正确，但覆盖写入
                // 不能当作追加数据检测，停止喂入并保留原因。
                _seekDetected = true;
                _session.MarkSeekDetected();
            }
            _fileStream.Seek(target, SeekOrigin.Begin);
            _position = target;
            return target;
        }
        catch (Exception exception)
        {
            _writeException ??= exception;
            // 托管异常不得穿过原生回调边界。
            return ffmpeg.AVERROR_EXTERNAL;
        }
    }
}
