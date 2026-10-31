using System;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Threading;
using FFmpeg.AutoGen.Abstractions;
using SkiaSharp;
using TSCutter.GUI.FFmpeg;

namespace TSCutter.GUI.Rendering.Gpu;

/// <summary>在当前 CGL 上下文中借用 VideoToolbox 的 IOSurface 平面，不回读像素。</summary>
internal sealed unsafe partial class MacOSVideoTexture : IDisposable
{
    private const string OpenGL = "/System/Library/Frameworks/OpenGL.framework/OpenGL";
    private const string CoreVideo = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";
    private const uint TextureRectangle = 0x84f5, TextureBindingRectangle = 0x84f6;
    private const uint TextureMinFilter = 0x2801, TextureMagFilter = 0x2800;
    private const uint TextureWrapS = 0x2802, TextureWrapT = 0x2803;
    private const int Nearest = 0x2600, ClampToEdge = 0x812f;
    private const uint Red = 0x1903, Rg = 0x8227, R8 = 0x8229, R16 = 0x822a, Rg8 = 0x822b, Rg16 = 0x822c;
    private const uint UnsignedByte = 0x1401, UnsignedShort = 0x1403;
    private const uint Nv12VideoRange = 0x34323076, Nv12FullRange = 0x34323066; // '420v', '420f'
    private const uint P010VideoRange = 0x78343230, P010FullRange = 0x78663230; // 'x420', 'xf20'
    private static int available, disabled;
    private uint yTexture, uvTexture;
    private GRContext? ownerContext;
    private Frame? retainedSource;
    private TextureReaper? reaper;
    private int disposed;
    internal SKImage Y { get; private set; } = null!;
    internal SKImage Uv { get; private set; } = null!;

    internal static bool IsAvailable => Volatile.Read(ref available) != 0 && Volatile.Read(ref disabled) == 0;

    internal static void Probe(GRContext? context)
    {
        var supported = OperatingSystem.IsMacOS() && context is { Backend: GRBackend.OpenGL, IsAbandoned: false }
            && CGLGetCurrentContext() != 0;
        Volatile.Write(ref available, supported ? 1 : 0);
    }

    internal static void Disable(string reason)
    {
        if (Interlocked.Exchange(ref disabled, 1) == 0)
            Console.WriteLine("VideoToolbox texture import unavailable; using YUV upload: " + reason);
    }

    internal static bool CanImport(Frame source) => IsAvailable && TryDescribe(source, out _, out _);

    internal static bool TryDescribe(Frame source, out AVPixelFormat format, out AVColorRange range)
    {
        format = AVPixelFormat.AV_PIX_FMT_NONE;
        range = AVColorRange.AVCOL_RANGE_UNSPECIFIED;
        AVFrame* frame = source;
        if (!OperatingSystem.IsMacOS() || frame->format != (int)AVPixelFormat.AV_PIX_FMT_VIDEOTOOLBOX
            || frame->hw_frames_ctx == null || frame->data[3] == null || frame->width <= 0 || frame->height <= 0)
            return false;
        var buffer = (nint)frame->data[3];
        var type = CVPixelBufferGetPixelFormatType(buffer);
        format = type switch
        {
            Nv12VideoRange or Nv12FullRange => AVPixelFormat.AV_PIX_FMT_NV12,
            P010VideoRange or P010FullRange => AVPixelFormat.AV_PIX_FMT_P010LE,
            _ => AVPixelFormat.AV_PIX_FMT_NONE
        };
        range = type is Nv12FullRange or P010FullRange ? AVColorRange.AVCOL_RANGE_JPEG : AVColorRange.AVCOL_RANGE_MPEG;
        return format != AVPixelFormat.AV_PIX_FMT_NONE && CVPixelBufferGetPlaneCount(buffer) == 2
            && CVPixelBufferGetIOSurface(buffer) != 0;
    }

    internal static MacOSVideoTexture Import(GRContext context, Frame source)
    {
        if (!TryDescribe(source, out var format, out _))
            throw new NotSupportedException("Unsupported VideoToolbox pixel buffer.");
        var cgl = CGLGetCurrentContext();
        if (cgl == 0 || context.Backend != GRBackend.OpenGL)
            throw new NotSupportedException("The renderer has no compatible CGL context.");
        var result = new MacOSVideoTexture
        {
            ownerContext = context,
            reaper = TextureReaper.For(cgl),
            retainedSource = source.CloneReference()
        };
        AVFrame* frame = source;
        var buffer = (nint)frame->data[3];
        var surface = CVPixelBufferGetIOSurface(buffer);
        var wide = format == AVPixelFormat.AV_PIX_FMT_P010LE;
        glGetIntegerv(TextureBindingRectangle, out var previousBinding);
        try
        {
            result.Y = CreatePlane(context, cgl, buffer, surface, 0, wide, ref result.yTexture);
            result.Uv = CreatePlane(context, cgl, buffer, surface, 1, wide, ref result.uvTexture);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
        finally
        {
            glBindTexture(TextureRectangle, (uint)previousBinding);
            context.ResetContext(GRGlBackendState.TextureBinding);
        }
    }

    private static SKImage CreatePlane(GRContext context, nint cgl, nint buffer, nint surface,
        nuint plane, bool wide, ref uint texture)
    {
        var width = checked((int)CVPixelBufferGetWidthOfPlane(buffer, plane));
        var height = checked((int)CVPixelBufferGetHeightOfPlane(buffer, plane));
        var internalFormat = plane == 0 ? wide ? R16 : R8 : wide ? Rg16 : Rg8;
        glGenTextures(1, out texture);
        glBindTexture(TextureRectangle, texture);
        var error = CGLTexImageIOSurface2D(cgl, TextureRectangle, internalFormat, width, height,
            plane == 0 ? Red : Rg, wide ? UnsignedShort : UnsignedByte, surface, (uint)plane);
        if (error != 0)
            throw new NotSupportedException($"Cannot bind IOSurface plane {plane}: CGL error {error}.");
        glTexParameteri(TextureRectangle, TextureMinFilter, Nearest);
        glTexParameteri(TextureRectangle, TextureMagFilter, Nearest);
        glTexParameteri(TextureRectangle, TextureWrapS, ClampToEdge);
        glTexParameteri(TextureRectangle, TextureWrapT, ClampToEdge);
        using var backend = new GRBackendTexture(width, height, false, new GRGlTextureInfo(TextureRectangle, texture, internalFormat));
        return SKImage.FromTexture(context, backend, GRSurfaceOrigin.TopLeft,
            plane == 0 ? wide ? SKColorType.Alpha16 : SKColorType.Gray8 : wide ? SKColorType.Rg1616 : SKColorType.Rg88,
            plane == 0 && wide ? SKAlphaType.Premul : SKAlphaType.Opaque)
            ?? throw new NotSupportedException("Skia cannot sample the IOSurface texture format.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        Y?.Dispose();
        Uv?.Dispose();
        // Skia 命令已提交。共享 CGL 上下文等待 GPU 栅栏后再释放纹理和 CVPixelBuffer，
        // 不在 Avalonia 渲染线程逐帧等待 GPU，也不让解码器过早复用该表面。
        var fence = glFenceSync(SyncGpuCommandsComplete, 0);
        glFlush();
        var retired = new RetiredTexture(yTexture, uvTexture, fence, retainedSource!);
        if (fence == 0 || reaper is null || !reaper.TryRetire(retired))
        {
            // GPU 严重积压时限制在途资源，退回同步回收，不无限保留解码表面。
            glFinish();
            retired.Release();
        }
        retainedSource = null;
        yTexture = uvTexture = 0;
        ownerContext?.ResetContext(GRGlBackendState.TextureBinding);
    }

    private const uint SyncGpuCommandsComplete = 0x9117;
    private const uint AlreadySignaled = 0x911a, ConditionSatisfied = 0x911c, WaitFailed = 0x911d;

    private sealed record RetiredTexture(uint Y, uint Uv, nint Fence, Frame Source)
    {
        internal void Release()
        {
            if (Fence != 0)
                glDeleteSync(Fence);
            var y = Y;
            var uv = Uv;
            if (y != 0)
                glDeleteTextures(1, ref y);
            if (uv != 0)
                glDeleteTextures(1, ref uv);
            Source.Dispose();
        }
    }

    private sealed class TextureReaper
    {
        private static readonly ConcurrentDictionary<nint, Lazy<TextureReaper>> Reapers = new();
        private readonly Channel<RetiredTexture> retired = Channel.CreateBounded<RetiredTexture>(
            new BoundedChannelOptions(3) { SingleReader = true, SingleWriter = false });
        private readonly nint context;
        private RetiredTexture? failedTexture;

        private TextureReaper(nint sharedContext)
        {
            var error = CGLCreateContext(CGLGetPixelFormat(sharedContext), sharedContext, out context);
            if (error != 0 || context == 0)
                throw new NotSupportedException($"Cannot create a shared CGL cleanup context: {error}.");
            // 仅负责当前渲染上下文的 GL 资源回收，不借用或切换 Avalonia 的上下文。
            var worker = new Thread(Run) { IsBackground = true, Name = "Video texture retirement" };
            worker.Start();
        }

        internal static TextureReaper For(nint context) => Reapers.GetOrAdd(CGLGetShareGroup(context),
            static (_, parent) => new Lazy<TextureReaper>(() => new TextureReaper(parent)), context).Value;
        internal bool TryRetire(RetiredTexture texture) => retired.Writer.TryWrite(texture);

        private void Run()
        {
            if (CGLSetCurrentContext(context) != 0)
            {
                Disable("Cannot activate the shared CGL cleanup context.");
                retired.Writer.TryComplete();
                return;
            }
            while (retired.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (retired.Reader.TryRead(out var texture))
                {
                    while (true)
                    {
                        // 长超时等待在部分驱动上会忙等；仅回收线程短暂休眠后做无阻塞检查。
                        Thread.Sleep(2);
                        var status = glClientWaitSync(texture.Fence, 0, 0);
                        if (status is AlreadySignaled or ConditionSatisfied)
                            break;
                        if (status == WaitFailed)
                        {
                            // 无法判断完成状态时关闭直传；持有该表面到进程退出，避免回收仍在使用的内存。
                            failedTexture = texture;
                            retired.Writer.TryComplete();
                            Disable("Cannot wait for a VideoToolbox texture fence.");
                            return;
                        }
                    }
                    texture.Release();
                }
            }
        }
    }

    [LibraryImport(CoreVideo)] private static partial uint CVPixelBufferGetPixelFormatType(nint buffer);
    [LibraryImport(CoreVideo)] private static partial nuint CVPixelBufferGetPlaneCount(nint buffer);
    [LibraryImport(CoreVideo)] private static partial nuint CVPixelBufferGetWidthOfPlane(nint buffer, nuint plane);
    [LibraryImport(CoreVideo)] private static partial nuint CVPixelBufferGetHeightOfPlane(nint buffer, nuint plane);
    [LibraryImport(CoreVideo)] private static partial nint CVPixelBufferGetIOSurface(nint buffer);
    [LibraryImport(OpenGL)] private static partial nint CGLGetCurrentContext();
    [LibraryImport(OpenGL)] private static partial nint CGLGetPixelFormat(nint context);
    [LibraryImport(OpenGL)] private static partial nint CGLGetShareGroup(nint context);
    [LibraryImport(OpenGL)] private static partial int CGLCreateContext(nint format, nint sharedContext, out nint context);
    [LibraryImport(OpenGL)] private static partial int CGLSetCurrentContext(nint context);
    [LibraryImport(OpenGL)] private static partial nint glFenceSync(uint condition, uint flags);
    [LibraryImport(OpenGL)] private static partial uint glClientWaitSync(nint fence, uint flags, ulong timeout);
    [LibraryImport(OpenGL)] private static partial void glDeleteSync(nint fence);
    [LibraryImport(OpenGL)] private static partial void glFlush();
    [LibraryImport(OpenGL)] private static partial void glFinish();
    [LibraryImport(OpenGL)] private static partial int CGLTexImageIOSurface2D(nint context, uint target, uint internalFormat,
        int width, int height, uint format, uint type, nint surface, uint plane);
    [LibraryImport(OpenGL)] private static partial void glGenTextures(int count, out uint texture);
    [LibraryImport(OpenGL)] private static partial void glDeleteTextures(int count, ref uint texture);
    [LibraryImport(OpenGL)] private static partial void glBindTexture(uint target, uint texture);
    [LibraryImport(OpenGL)] private static partial void glTexParameteri(uint target, uint parameter, int value);
    [LibraryImport(OpenGL)] private static partial void glGetIntegerv(uint parameter, out int value);
}
