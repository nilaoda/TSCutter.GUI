using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen.Abstractions;
using SkiaSharp;
using TSCutter.GUI.FFmpeg;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;
using static TSCutter.GUI.Rendering.Gpu.WindowsAngle;

namespace TSCutter.GUI.Rendering.Gpu;

/// <summary>将 D3D11 解码表面的 Y/UV 平面导入 ANGLE，支持直接采样和 GPU 内复制。</summary>
internal sealed unsafe class WindowsVideoTexture : IDisposable
{
    private static readonly object DeviceSync = new();
    private static nint renderDevice, renderDisplay;
    private static int available;
    private static int failureReported;
    private static int retirementFailed;
    private readonly Pool pool;
    private readonly Slot slot;
    private readonly GRContext context;
    internal SKImage Y { get; }
    internal SKImage Uv { get; }
    internal bool Direct { get; }
    private bool disposed;

    internal static bool IsAvailable => Volatile.Read(ref available) != 0;
    internal static string? Capability { get; private set; }

    internal static void Probe(GRContext? context)
    {
        if (!OperatingSystem.IsWindows() || Volatile.Read(ref retirementFailed) != 0)
            return;
        try
        {
            if (context is not { Backend: GRBackend.OpenGL, IsAbandoned: false })
            {
                Volatile.Write(ref available, 0);
                return;
            }
            var display = Display();
            if (display == 0 || Context() == 0)
            {
                Volatile.Write(ref available, 0);
                return;
            }
            lock (DeviceSync)
            {
                if (QueryDisplay(display, 0x322c, out var device) == 0
                    || QueryDevice(device, 0x33a1, out var d3d) == 0 || d3d == 0)
                    throw new NotSupportedException("Cannot query the actual ANGLE D3D11 device.");
                if (display == renderDisplay && d3d == renderDevice)
                {
                    if (Volatile.Read(ref failureReported) == 0)
                        Volatile.Write(ref available, 1);
                    return;
                }
                var extensions = Text(QueryString(display, 0x3055));
                var client = Text(QueryString(0, 0x3055));
                if (!Has(extensions, "EGL_ANGLE_image_d3d11_texture")
                    || !Has(extensions, "EGL_KHR_image_base")
                    || !Has(client, "EGL_EXT_device_query") || !Has(Text(glGetString(0x1f03)), "GL_OES_EGL_image"))
                    throw new NotSupportedException("The current ANGLE context lacks D3D11 plane images.");
                WindowsD3D11.ProtectContext(d3d);
                WindowsD3D11.AddRef(d3d);
                WindowsD3D11.Release(renderDevice);
                renderDevice = d3d;
                renderDisplay = display;
                Capability = $"{Text(QueryString(display, 0x3054))}; {WindowsD3D11.Adapter(d3d)}";
                Volatile.Write(ref available, 1);
                Volatile.Write(ref failureReported, 0);
                Console.WriteLine("Windows video interop: " + Capability);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Volatile.Write(ref available, 0);
            Capability = exception.Message;
        }
    }

    internal sealed class TexturesBusyException() : Exception("D3D11 video textures are still in flight.");
    internal static void Disable(string reason)
    {
        Volatile.Write(ref available, 0);
        if (Interlocked.Exchange(ref failureReported, 1) == 0)
            Console.WriteLine("Windows surface import unavailable; using hardware readback: " + reason);
    }

    // 将渲染器的设备引用交给 FFmpeg，并由 FFmpeg 初始化递归上下文锁。
    internal static bool TryAttachDecoder(CodecContext codec, bool allowDirect = true)
    {
        if (!OperatingSystem.IsWindows() || !IsAvailable)
            return false;
        AVBufferRef* buffer = null;
        try
        {
            lock (DeviceSync)
            {
                if (renderDevice == 0)
                    return false;
                WindowsD3D11.CheckDevice(renderDevice);
                buffer = FF.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
                if (buffer == null)
                    return false;
                var device = (AVD3D11VADeviceContext*)((AVHWDeviceContext*)buffer->data)->hwctx;
                WindowsD3D11.AddRef(renderDevice);
                device->device = (ID3D11Device*)renderDevice;
                device->BindFlags = allowDirect ? WindowsD3D11.ShaderResource : 0;
            }
            FFmpegException.Check(FF.av_hwdevice_ctx_init(buffer));
            AVCodecContext* raw = codec;
            raw->hw_device_ctx = buffer;
            buffer = null;
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.WriteLine("Renderer decode device unavailable; using the default hardware device.");
            return false;
        }
        finally
        {
            if (buffer != null)
                FF.av_buffer_unref(&buffer);
        }
    }

    internal static bool CanImport(Frame frame) => IsAvailable && TryDescribe(frame, out _, out _);
    internal static bool TryDescribe(Frame source, out AVPixelFormat format, out AVColorRange range)
    {
        AVFrame* frame = source;
        format = AVPixelFormat.AV_PIX_FMT_NONE;
        range = frame->color_range;
        if (!OperatingSystem.IsWindows()
            || frame->format != (int)AVPixelFormat.AV_PIX_FMT_D3D11
            || frame->hw_frames_ctx == null || frame->data[0] == null || frame->width <= 0 || frame->height <= 0
            || frame->crop_left != 0 || frame->crop_top != 0 || frame->crop_right != 0 || frame->crop_bottom != 0)
            return false; // 存在尚未应用的裁剪时，沿用 FFmpeg 的回读和裁剪处理。
        var frames = (AVHWFramesContext*)frame->hw_frames_ctx->data;
        format = frames->sw_format;
        return format is AVPixelFormat.AV_PIX_FMT_NV12 or AVPixelFormat.AV_PIX_FMT_P010LE;
    }

    internal sealed class Owner(bool allowDirect = true) : IDisposable
    {
        private readonly Dictionary<nint, Pool> pools = new();
        private int references = 1;
        internal Owner Retain()
        {
            var count = Volatile.Read(ref references);
            while (count > 0)
            {
                var observed = Interlocked.CompareExchange(ref references, count + 1, count);
                if (observed == count)
                    return this;
                count = observed;
            }
            throw new ObjectDisposedException(nameof(Owner));
        }
        internal WindowsVideoTexture Import(GRContext context, Frame source, bool allowDirect)
        {
            if (!IsAvailable)
                throw new NotSupportedException("Windows surface import is unavailable.");
            // 只在持有 Skia 渲染租约的线程上访问纹理池和 GL/EGL 对象。
            var key = Context();
            if (!pools.TryGetValue(key, out var pool))
            {
                if (pools.Count >= 2)
                    throw new NotSupportedException("The video rendering context changed repeatedly.");
                pools.Add(key, pool = new Pool());
            }
            return new WindowsVideoTexture(context, source, pool, allowDirect && this.allowDirect);
        }
        private readonly bool allowDirect = allowDirect;
        public void Dispose()
        {
            if (Interlocked.Decrement(ref references) != 0)
                return;
            foreach (var pool in pools.Values)
                pool.Complete();
        }
    }

    private WindowsVideoTexture(GRContext context, Frame source, Pool pool, bool allowDirect)
    {
        if (!TryDescribe(source, out var format, out _))
            throw new NotSupportedException("Unsupported D3D11 frame geometry or format.");
        this.context = context;
        this.pool = pool;
        AVFrame* frame = source;
        var texture = (nint)frame->data[0];
        var description = WindowsD3D11.Describe(texture);
        var slice = (nuint)frame->data[1];
        if (description.Format != (format == AVPixelFormat.AV_PIX_FMT_NV12 ? 103u : 104u)
            || description.MipLevels != 1 || slice >= description.ArraySize || description.SampleCount != 1
            || frame->width > description.Width || frame->height > description.Height)
            throw new NotSupportedException("Unsupported D3D11 texture layout.");
        var device = WindowsD3D11.GetDevice(texture);
        try
        {
            if (device != pool.Device)
                throw new NotSupportedException("Decode and rendering COM devices differ; using hardware readback.");
        }
        finally
        {
            WindowsD3D11.Release(device);
        }
        slot = pool.Rent();
        glGetIntegerv(0x8069, out var previous);
        try
        {
            slot.Source = source.CloneReference();
            Direct = allowDirect && (description.BindFlags & WindowsD3D11.ShaderResource) != 0 && !pool.DirectFailed;
            if (Direct)
            {
                try
                {
                    slot.Import(context, texture, description, (int)slice);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    pool.DisableDirect(exception.Message);
                    slot.ClearImages();
                    Direct = false;
                }
            }
            if (!Direct)
            {
                slot.PrepareCopy(description);
                // 复制操作与解码器共用 FFmpeg 的递归上下文锁。
                var frames = (AVHWFramesContext*)frame->hw_frames_ctx->data;
                var hw = (AVD3D11VADeviceContext*)frames->device_ctx->hwctx;
                ((delegate* unmanaged[Cdecl]<void*, void>)hw->@lock.Pointer)(hw->lock_ctx);
                try
                {
                    WindowsD3D11.Copy(pool.Immediate, slot.CopyTexture, texture, (uint)slice);
                }
                finally
                {
                    ((delegate* unmanaged[Cdecl]<void*, void>)hw->unlock.Pointer)(hw->lock_ctx);
                }
                slot.Import(context, slot.CopyTexture, slot.CopyDescription, 0);
            }
            Y = slot.Y!;
            Uv = slot.Uv!;
        }
        catch
        {
            Retire();
            throw;
        }
        finally
        {
            glBindTexture(Texture2D, (uint)previous);
            context.ResetContext(GRGlBackendState.TextureBinding);
        }
    }

    internal void DisableDirect(string reason) => pool.DisableDirect(reason);
    private void Retire()
    {
        // 先提交 ANGLE 命令，再在同一 D3D11 上下文插入完成查询。
        // GL/EGL 对象在渲染线程释放；源帧保留到 GPU 完成，防止解码器提前复用切片。
        glFlush();
        slot.ClearImages();
        WindowsD3D11.Signal(pool.Immediate, slot.CompletionQuery);
        pool.Retire(slot);
    }
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Retire();
        context.ResetContext(GRGlBackendState.TextureBinding);
    }

    private sealed class Pool
    {
        internal readonly nint Device, Immediate, DisplayHandle;
        private readonly ConcurrentQueue<Slot> idle = new();
        private readonly List<Slot> pending = new();
        private readonly object retirementSync = new();
        private readonly AutoResetEvent wake = new(false);
        private readonly List<Slot> slots = new();
        private bool complete;
        private int failed;
        internal bool DirectFailed { get; private set; }
        // 无法确认 GPU 完成时保留池内引用，并禁用后续导入，避免新会话持续累积隔离池。
        private static readonly ConcurrentBag<Pool> Quarantine = new();
        internal Pool()
        {
            DisplayHandle = Display();
            nint eglDevice = 0, device = 0;
            if (QueryDisplay(DisplayHandle, 0x322c, out eglDevice) == 0
                || QueryDevice(eglDevice, 0x33a1, out device) == 0 || device == 0)
                throw new NotSupportedException("No current ANGLE D3D11 device.");
            Device = device;
            WindowsD3D11.AddRef(Device);
            try
            {
                WindowsD3D11.ProtectContext(Device);
                Immediate = WindowsD3D11.ImmediateContext(Device);
                new Thread(Reap) { IsBackground = true, Name = "D3D11 video retirement" }.Start();
            }
            catch
            {
                WindowsD3D11.Release(Immediate);
                WindowsD3D11.Release(Device);
                throw;
            }
        }
        internal Slot Rent()
        {
            if (Volatile.Read(ref failed) != 0)
                throw new NotSupportedException("D3D11 video retirement context failed.");
            WindowsD3D11.CheckDevice(Device);
            DrainCompleted();
            if (idle.TryDequeue(out var slot))
                return slot;
            if (slots.Count >= 4)
                throw new TexturesBusyException();
            slot = new Slot(this);
            slots.Add(slot);
            return slot;
        }
        internal void DisableDirect(string reason)
        {
            if (DirectFailed)
                return;
            DirectFailed = true;
            Console.WriteLine("D3D11 direct sampling unavailable; trying GPU copy: " + reason);
        }
        internal void Retire(Slot slot)
        {
            lock (retirementSync)
            {
                slot.RetiredAt = Stopwatch.GetTimestamp();
                pending.Add(slot);
                wake.Set();
            }
        }
        internal void Complete()
        {
            lock (retirementSync)
            {
                complete = true;
                wake.Set();
            }
        }
        private bool DrainCompleted()
        {
            lock (retirementSync)
            {
                for (var index = pending.Count - 1; index >= 0; index--)
                {
                    var slot = pending[index];
                    if (!WindowsD3D11.IsComplete(Immediate, slot.CompletionQuery))
                    {
                        if (Stopwatch.GetElapsedTime(slot.RetiredAt).TotalSeconds > 30)
                            throw new TimeoutException("D3D11 video completion exceeded 30 seconds.");
                        continue;
                    }
                    slot.Source?.Dispose();
                    slot.Source = null;
                    pending.RemoveAt(index);
                    idle.Enqueue(slot);
                }
                return complete && pending.Count == 0;
            }
        }
        private void Reap()
        {
            var safe = false;
            try
            {
                while (!DrainCompleted())
                {
                    WindowsD3D11.CheckDevice(Device);
                    bool hasPending;
                    lock (retirementSync)
                        hasPending = pending.Count > 0;
                    wake.WaitOne(hasPending ? 1 : Timeout.Infinite);
                }
                safe = true;
            }
            catch (Exception exception)
            {
                Volatile.Write(ref failed, 1);
                Console.WriteLine("D3D11 video retirement unavailable; using hardware readback: " + exception.Message);
                var removed = false;
                try
                {
                    WindowsD3D11.CheckDevice(Device);
                }
                catch (COMException)
                {
                    removed = true;
                }
                if (removed)
                {
                    // 设备移除后，待执行命令已取消；仍需等所有渲染租约释放再销毁纹理池。
                    while (true)
                    {
                        lock (retirementSync)
                        {
                            if (complete)
                                break;
                        }
                        wake.WaitOne();
                    }
                    safe = true;
                }
                else
                {
                    Volatile.Write(ref retirementFailed, 1);
                    Disable("GPU completion could not be established.");
                    Quarantine.Add(this);
                }
            }
            finally
            {
                if (safe)
                {
                    foreach (var slot in slots)
                        slot.Dispose();
                    WindowsD3D11.Release(Immediate);
                    WindowsD3D11.Release(Device);
                    wake.Dispose();
                }
            }
        }
    }

    private sealed class Slot(Pool pool) : IDisposable
    {
        internal Frame? Source;
        internal long RetiredAt;
        internal nint CopyTexture;
        internal readonly nint CompletionQuery = WindowsD3D11.CreateCompletionQuery(pool.Device);
        internal WindowsD3D11.TextureDescription CopyDescription;
        internal SKImage? Y, Uv;
        private nint yImage, uvImage;
        private uint yTexture, uvTexture;
        internal void PrepareCopy(WindowsD3D11.TextureDescription description)
        {
            description.ArraySize = 1;
            description.BindFlags = WindowsD3D11.ShaderResource;
            description.Usage = description.CpuAccessFlags = description.MiscFlags = 0;
            if (CopyTexture != 0 && CopyDescription.Width == description.Width && CopyDescription.Height == description.Height
                && CopyDescription.Format == description.Format)
                return;
            WindowsD3D11.Release(CopyTexture);
            CopyTexture = 0;
            CopyTexture = WindowsD3D11.CreateTexture(pool.Device, description);
            CopyDescription = description;
        }
        internal void Import(GRContext context, nint texture, WindowsD3D11.TextureDescription description, int slice)
        {
            var wide = description.Format == 104;
            Y = Plane(context, texture, (int)description.Width, (int)description.Height, 0, slice, wide, ref yImage, ref yTexture);
            Uv = Plane(context, texture, (int)description.Width / 2, (int)description.Height / 2, 1, slice, wide, ref uvImage, ref uvTexture);
        }
        private SKImage Plane(GRContext context, nint texture, int width, int height, int plane, int slice,
            bool wide, ref nint image, ref uint glTexture)
        {
            int* attributes = stackalloc int[] { 0x3492, plane, 0x3493, slice, None };
            image = CreateImage(pool.DisplayHandle, 0, 0x3484, texture, attributes);
            if (image == 0)
                throw new NotSupportedException($"Cannot import D3D11 plane {plane}, slice {slice}: EGL 0x{Error():x}.");
            glGenTextures(1, out glTexture);
            glBindTexture(Texture2D, glTexture);
            glEGLImageTargetTexture2DOES(Texture2D, image);
            CheckGl();
            glTexParameteri(Texture2D, 0x2801, 0x2600);
            glTexParameteri(Texture2D, 0x2800, 0x2600);
            glTexParameteri(Texture2D, 0x2802, 0x812f);
            glTexParameteri(Texture2D, 0x2803, 0x812f);
            var format = plane == 0 ? wide ? 0x822au : 0x8229u : wide ? 0x822cu : 0x822bu;
            using var backend = new GRBackendTexture(width, height, false, new GRGlTextureInfo(Texture2D, glTexture, format));
            return SKImage.FromTexture(context, backend, GRSurfaceOrigin.TopLeft,
                plane == 0 ? wide ? SKColorType.Alpha16 : SKColorType.Gray8 : wide ? SKColorType.Rg1616 : SKColorType.Rg88,
                plane == 0 && wide ? SKAlphaType.Premul : SKAlphaType.Opaque)
                ?? throw new NotSupportedException("Skia cannot sample the imported D3D11 plane.");
        }
        private void ClearSkia()
        {
            Y?.Dispose();
            Y = null;
            Uv?.Dispose();
            Uv = null;
        }
        internal void ClearImages()
        {
            ClearSkia();
            if (yTexture != 0)
                glDeleteTextures(1, ref yTexture);
            if (uvTexture != 0)
                glDeleteTextures(1, ref uvTexture);
            yTexture = uvTexture = 0;
            if (yImage != 0)
                DestroyImage(pool.DisplayHandle, yImage);
            if (uvImage != 0)
                DestroyImage(pool.DisplayHandle, uvImage);
            yImage = uvImage = 0;
        }
        public void Dispose()
        {
            Source?.Dispose();
            WindowsD3D11.Release(CopyTexture);
            WindowsD3D11.Release(CompletionQuery);
            CopyTexture = 0;
        }
    }
}
