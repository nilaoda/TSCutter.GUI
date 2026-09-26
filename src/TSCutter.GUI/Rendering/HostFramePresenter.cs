using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Swscales;
using Sdcb.FFmpeg.Utils;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Rendering;

/// <summary>
/// Low-allocation host-memory presenter used after a hardware frame has been
/// transferred to system memory. Two bitmap buffers are alternated so the
/// decoder never writes the bitmap currently published to the UI.
/// </summary>
public sealed unsafe class HostFramePresenter : IDisposable
{
    private const int MaximumBufferCount = 2;
    private readonly object sync = new();
    private readonly List<BufferSlot> buffers = [];
    private VideoFrameConverter? converter;
    private Frame? destinationFrame;
    private bool disposed;

    public bool TryConvert(
        Frame source,
        int maxWidth,
        int maxHeight,
        out IBitmapFrameLease? lease)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            var sourceSize = new PixelSize(source.Width, source.Height);
            var targetSize = maxWidth > 0 && maxHeight > 0
                ? ImageUtil.CalculateDecodeSize(sourceSize, new PixelSize(maxWidth, maxHeight))
                : sourceSize;
            if (targetSize.Width <= 0 || targetSize.Height <= 0)
            {
                lease = null;
                return false;
            }

            var slot = AcquireBuffer(targetSize);
            if (slot is null)
            {
                lease = null;
                return false;
            }

            try
            {
                using var framebuffer = slot.Bitmap!.Lock();
                ConfigureDestinationFrame(targetSize, framebuffer);
                converter ??= new VideoFrameConverter();
                converter.ConvertFrame(source, destinationFrame!);

                lease = new BitmapFrameLease(this, slot);
                return true;
            }
            catch
            {
                ReleaseBuffer(slot);
                throw;
            }
        }
    }

    private BufferSlot? AcquireBuffer(PixelSize size)
    {
        var slot = buffers.FirstOrDefault(candidate => !candidate.IsLeased && candidate.Size == size)
            ?? buffers.FirstOrDefault(candidate => !candidate.IsLeased);

        if (slot is null)
        {
            if (buffers.Count >= MaximumBufferCount)
                return null;

            slot = new BufferSlot();
            buffers.Add(slot);
        }

        if (slot.Size != size || slot.Bitmap is null)
        {
            slot.Bitmap?.Dispose();
            slot.Bitmap = CreateBitmap(size);
            slot.Size = size;
        }

        slot.IsLeased = true;
        return slot;
    }

    private static WriteableBitmap CreateBitmap(PixelSize size) => new(
        size,
        new Vector(96, 96),
        PixelFormat.Bgra8888,
        AlphaFormat.Opaque);

    private void ConfigureDestinationFrame(PixelSize size, ILockedFramebuffer framebuffer)
    {
        destinationFrame ??= new Frame();
        destinationFrame.Width = size.Width;
        destinationFrame.Height = size.Height;
        destinationFrame.Format = (int)AVPixelFormat.Bgra;
        destinationFrame.Data[0] = framebuffer.Address;
        destinationFrame.Linesize[0] = framebuffer.RowBytes;
    }

    private void ReleaseBuffer(BufferSlot slot)
    {
        lock (sync)
        {
            if (!slot.IsLeased)
                return;

            slot.IsLeased = false;
            if (disposed)
            {
                slot.Bitmap?.Dispose();
                slot.Bitmap = null;
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;

            disposed = true;
            foreach (var slot in buffers.Where(slot => !slot.IsLeased))
            {
                slot.Bitmap?.Dispose();
                slot.Bitmap = null;
            }
            destinationFrame?.Dispose();
            converter?.Dispose();
            destinationFrame = null;
            converter = null;
        }
    }

    private sealed class BufferSlot
    {
        public WriteableBitmap? Bitmap { get; set; }
        public PixelSize Size { get; set; }
        public bool IsLeased { get; set; }
    }

    private sealed class BitmapFrameLease(HostFramePresenter owner, BufferSlot slot) : IBitmapFrameLease
    {
        private HostFramePresenter? owner = owner;

        public Bitmap Bitmap => slot.Bitmap
            ?? throw new ObjectDisposedException(nameof(BitmapFrameLease));

        public void Dispose()
        {
            var currentOwner = System.Threading.Interlocked.Exchange(ref owner, null);
            currentOwner?.ReleaseBuffer(slot);
        }
    }
}

public interface IBitmapFrameLease : IDisposable
{
    Bitmap Bitmap { get; }
}
