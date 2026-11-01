using System.Runtime.InteropServices;
using Avalonia;
using FFmpeg.AutoGen.Abstractions;
using SkiaSharp;
using TSCutter.GUI.Extensions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Rendering.Gpu;
using TSCutter.GUI.Tests.FFmpeg;
using Xunit;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Tests.Preview;

[Collection("Avalonia UI")]
public sealed unsafe class WindowsVideoTextureTests
{
    [WindowsGpuFact]
    public void RealDecodePlanesMatchUploadAndFallbackReportsTheActualPath()
    {
        using var egl = new EglContext();
        using var gl = GRGlInterface.CreateGles(egl.GlProcedure);
        using var context = GRContext.CreateGl(gl);
        Assert.NotNull(context);
        HardwareVideoTexture.Probe(context);
        Assert.True(WindowsVideoTexture.IsAvailable, WindowsVideoTexture.Capability);
        // 不带 SRV 的表面走 GPU 内复制，带 SRV 的表面直接采样；设备对象不同则回读。
        foreach (var binding in new[] { 0, 1, 2 })
        {
            using var input = FormatContext.OpenInputUrl(Environment.GetEnvironmentVariable("TSCUTTER_WINDOWS_GPU_SAMPLE")!);
            input.LoadStreamInfo();
            var stream = input.GetVideoStream();
            using var decoder = new CodecContext(Codec.FindDecoderById(stream.Codecpar!.CodecId));
            decoder.FillParameters(stream.Codecpar);
            decoder.PacketTimeBase = stream.TimeBase;
            if (binding == 2)
                decoder.AttachHardwareDevice(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
            else
                Assert.True(WindowsVideoTexture.TryAttachDecoder(decoder, binding == 1));
            decoder.Open();
            using var source = FirstFrame(input, stream.Index, decoder);
            Assert.True(source.HasHardwareFramesContext);
            using var software = source.TransferToSoftwareFrame();
            var size = new PixelSize(320, 180);
            var info = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var target = SKSurface.Create(context, true, info);
            using var expectedPresenter = new SkiaVideoFramePresenter();
            var expected = Render(expectedPresenter, software, VideoPresentationMode.SoftwareGpu, 0);
            using var actualPresenter = new SkiaVideoFramePresenter();
            var mode = binding switch
            {
                0 => VideoPresentationMode.HardwareGpuCopy,
                1 => VideoPresentationMode.HardwareGpu,
                _ => VideoPresentationMode.HardwareGpuUpload
            };
            var actual = Render(actualPresenter, source, mode, binding == 2 ? 1 : 0);
            for (var index = 0; index < actual.Length; index++)
                Assert.InRange(Math.Abs(actual[index] - expected[index]), 0, 2);

            byte[] Render(SkiaVideoFramePresenter presenter, Frame frame, VideoPresentationMode expectedMode, int readbacks)
            {
                Assert.True(presenter.TryCreateFrame(frame, true, size, false, (_, _) => { }, () => { }, out var image));
                using (image)
                {
                    image!.RenderBenchmark(context, target);
                    Assert.Equal(expectedMode, image.LastPresentationMode);
                    Assert.Equal(readbacks, image.HardwareReadbacks);
                    using var pixels = new SKBitmap(info);
                    Assert.True(target.ReadPixels(info, pixels.GetPixels(), pixels.RowBytes, 0, 0));
                    return pixels.Bytes;
                }
            }
        }
    }

    private static Frame FirstFrame(FormatContext input, int stream, CodecContext decoder)
    {
        using var packet = new Packet();
        using var frame = new Frame();
        while (FF.av_read_frame(input, packet) >= 0)
        {
            if (packet.StreamIndex == stream)
                foreach (var decoded in decoder.DecodePacket(packet, frame))
                    return decoded.CloneReference();
            packet.Unref();
        }
        throw new InvalidOperationException("The hardware test sample produced no frame.");
    }

    public sealed class WindowsGpuFactAttribute : NativeMediaFactAttribute
    {
        public WindowsGpuFactAttribute() : base(sampleVariable: "TSCUTTER_WINDOWS_GPU_SAMPLE")
        {
            if (!OperatingSystem.IsWindows())
                Skip = "Requires Windows D3D11/ANGLE.";
        }
    }

    private sealed class EglContext : IDisposable
    {
        private readonly nint module, display, context;
        internal EglContext()
        {
            module = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", "av_libglesv2.dll"));
            display = ((delegate* unmanaged[Stdcall]<nint, nint>)Egl("GetDisplay"))(0);
            int major, minor;
            Assert.NotEqual(0, ((delegate* unmanaged[Stdcall]<nint, int*, int*, int>)Egl("Initialize"))(display, &major, &minor));
            int* attributes = stackalloc int[] { 0x3033, 1, 0x3040, 4, 0x3038 };
            nint config = 0;
            int count;
            Assert.NotEqual(0, ((delegate* unmanaged[Stdcall]<nint, int*, nint*, int, int*, int>)Egl("ChooseConfig"))(display, attributes, &config, 1, &count));
            int* contextAttributes = stackalloc int[] { 0x3098, 3, 0x3038 };
            context = ((delegate* unmanaged[Stdcall]<nint, nint, nint, int*, nint>)Egl("CreateContext"))(display, config, 0, contextAttributes);
            Assert.NotEqual(0, context);
            Assert.NotEqual(0, MakeCurrent(context));
        }
        private nint Egl(string name) => NativeLibrary.GetExport(module, "EGL_" + name);
        private int MakeCurrent(nint value) => ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint, int>)Egl("MakeCurrent"))(display, 0, 0, value);
        internal nint GlProcedure(string name)
        {
            if (NativeLibrary.TryGetExport(module, name, out var address))
                return address;
            var text = Marshal.StringToCoTaskMemUTF8(name);
            try
            {
                return ((delegate* unmanaged[Stdcall]<nint, nint>)Egl("GetProcAddress"))(text);
            }
            finally
            {
                Marshal.FreeCoTaskMem(text);
            }
        }
        public void Dispose()
        {
            MakeCurrent(0);
            ((delegate* unmanaged[Stdcall]<nint, nint, int>)Egl("DestroyContext"))(display, context);
            ((delegate* unmanaged[Stdcall]<nint, int>)Egl("Terminate"))(display);
            NativeLibrary.Free(module);
        }
    }
}
