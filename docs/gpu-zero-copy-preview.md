# GPU preview presentation

The preview now has an explicit presentation boundary in
`TSCutter.GUI.Rendering.IGpuFramePresenter`:

```text
AVFrame (hardware surface)
        |
        +-- presenter accepts the native surface --> GPU presentation
        |
        +-- presenter rejects it ------------------> av_hwframe_transfer_data
                                                     -> existing bitmap path
```

`VideoInstance.IsHardwareDecoding` describes the decoder, while
`VideoInstance.PresentationMode` describes the presentation path. This keeps
hardware decoding status correct when the UI must use a CPU bitmap fallback.

When native GPU import is unavailable, the fallback now uses a host-memory
presenter. It reuses one software `AVFrame`, two `WriteableBitmap` buffers,
and one swscale context. swscale writes BGRA pixels directly into the locked
Avalonia framebuffer, so the old per-frame BGRA staging frame and final
staging-to-bitmap copy are removed. The unavoidable hardware-to-host transfer
still remains; this path is therefore low-allocation single-copy host
presentation, rather than true zero-copy.

The bitmap buffers are held by explicit leases and returned as soon as a
decoded result is replaced or discarded. The pool is capped at two buffers;
when the preview target size changes, only a currently unused buffer is
recreated. Historical sizes are never retained. The reusable software frame
also skips `av_frame_copy_props`, because FFmpeg appends frame side data to an
existing destination and metadata-heavy streams would otherwise grow memory
on every decoded preview.

## Why the default presenter is conservative

Avalonia 11.3 exposes external-image import for D3D11 shared handles,
IOSurface references, and Vulkan opaque file descriptors. The built-in
FFmpeg surfaces do not match that contract directly:

| OS | Decoder surface | Missing bridge |
| --- | --- | --- |
| Windows | D3D11VA NV12 texture, often an array slice | D3D11 texture export plus a YUV shader/view for the slice |
| macOS | VideoToolbox bi-planar CVPixelBuffer | IOSurface lifetime handling plus Metal YUV conversion |
| Linux | Software frame (the bundled FFmpeg has no Linux hardware decoding) | A future Linux package could add VA-API DMABUF export and EGL/Vulkan YUV import |

Importing these surfaces as RGBA/BGRA without conversion produces black or
incorrectly coloured frames. The default presenter therefore reports the
exact Avalonia handle types and deliberately declines the frame until the
platform bridge is available. The fallback remains automatic, including when
the UI renderer and decoder are on different GPU devices.

## Next implementation step

Add one presenter per platform that:

1. Creates a shared/importable surface in the decoder device (including a
   stable texture-array slice on D3D11).
2. Performs YUV-to-RGBA conversion in the UI GPU context, without
   `av_hwframe_transfer_data` or `WriteableBitmap`.
3. Holds the FFmpeg frame reference until Avalonia's imported image signals
   completion, then releases it.

Any failure in those steps must return `false`; `VideoInstance` will continue
with the existing software bitmap path instead of disabling the decoder or
showing a stale frame.
