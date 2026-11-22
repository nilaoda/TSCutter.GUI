#if TSCUTTER_GRAPHICS_STATIC
using System;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace TSCutter.GUI.Utils.Media;

internal static class NativeGraphicsSmokeTest
{
    internal static int Run()
    {
        try
        {
            using var typeface = SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default;
            using var bitmap = new SKBitmap(128, 48);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Black);
            using var paint = new SKPaint
            {
                Color = SKColors.White,
                Typeface = typeface,
                TextSize = 24,
                IsAntialias = true
            };
            canvas.DrawText("Static", 4, 32, paint);
            canvas.Flush();
            var painted = false;
            for (var y = 0; y < bitmap.Height; y++)
                for (var x = 0; x < bitmap.Width; x++)
                    painted |= bitmap.GetPixel(x, y) != SKColors.Black;
            if (!painted)
                throw new InvalidOperationException("Static font rendering produced no pixels.");
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            using var decoded = SKBitmap.Decode(encoded.ToArray());
            if (decoded is null || decoded.Width != bitmap.Width || decoded.Height != bitmap.Height)
                throw new InvalidOperationException("Static PNG encoding and decoding failed.");
            for (var y = 0; y < bitmap.Height; y++)
                for (var x = 0; x < bitmap.Width; x++)
                    if (decoded.GetPixel(x, y) != bitmap.GetPixel(x, y))
                        throw new InvalidOperationException("The static PNG round trip changed image pixels.");

            using var fontStream = typeface.OpenStream()
                                   ?? throw new InvalidOperationException("The default typeface has no font stream.");
            using var fontData = SKData.Create(fontStream);
            using var managedFont = fontData.AsStream();
            using var blob = HarfBuzzSharp.Blob.FromStream(managedFont);
            using var face = new HarfBuzzSharp.Face(blob, 0);
            using var font = new HarfBuzzSharp.Font(face);
            font.SetFunctionsOpenType();
            using var buffer = new HarfBuzzSharp.Buffer();
            buffer.AddUtf8("Static link");
            buffer.GuessSegmentProperties();
            font.Shape(buffer);
            if (buffer.GlyphInfos.Length == 0 || Array.TrueForAll(buffer.GlyphInfos, glyph => glyph.Codepoint == 0))
                throw new InvalidOperationException("Static text shaping produced no glyphs.");

            if (OperatingSystem.IsWindows() && EglGetProcAddress("glGetString") == IntPtr.Zero)
                throw new InvalidOperationException("The static ANGLE entry point is unavailable.");
            if (OperatingSystem.IsMacOS() && CreateAvaloniaNative() == IntPtr.Zero)
                throw new InvalidOperationException("The static native window factory is unavailable.");
            Console.WriteLine("Static graphics runtime verified.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    [DllImport("av_libglesv2", EntryPoint = "EGL_GetProcAddress", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr EglGetProcAddress(string name);

    [DllImport("libAvaloniaNative", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr CreateAvaloniaNative();
}
#endif
