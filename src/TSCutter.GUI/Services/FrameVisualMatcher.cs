using System;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace TSCutter.GUI.Services;

internal static class FrameVisualMatcher
{
    private const int Width = 32;
    private const int Height = 18;

    internal sealed class Fingerprint(byte[] luma, byte[] red, byte[] blue)
    {
        public byte[] Luma { get; } = luma;
        public byte[] Red { get; } = red;
        public byte[] Blue { get; } = blue;
    }

    public static Fingerprint Create(Bitmap bitmap, double displayAspectRatio = 0)
    {
        using var source = new SKBitmap(new SKImageInfo(
            bitmap.PixelSize.Width, bitmap.PixelSize.Height,
            SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var framebuffer = new SkiaFramebuffer(source))
            bitmap.CopyPixels(framebuffer, AlphaFormat.Opaque);
        return Create(source, displayAspectRatio);
    }

    internal static Fingerprint Create(SKBitmap bitmap, double displayAspectRatio = 0)
    {
        using var pixels = new SKBitmap(new SKImageInfo(
            Width, Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(pixels))
        {
            canvas.Clear(SKColors.Black);
            var aspect = displayAspectRatio > 0 && double.IsFinite(displayAspectRatio)
                ? displayAspectRatio : bitmap.Width / (double)bitmap.Height;
            var fittedWidth = Math.Min(Width, Height * aspect);
            var fittedHeight = Math.Min(Height, Width / aspect);
            canvas.DrawBitmap(bitmap, new SKRect(
                (float)((Width - fittedWidth) / 2), (float)((Height - fittedHeight) / 2),
                (float)((Width + fittedWidth) / 2), (float)((Height + fittedHeight) / 2)));
        }
        return CreatePixels(pixels);
    }

    private static Fingerprint CreatePixels(SKBitmap pixels)
    {
        var luma = new byte[Width * Height];
        var red = new byte[luma.Length];
        var blue = new byte[luma.Length];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var color = pixels.GetPixel(x, y);
            var index = y * Width + x;
            luma[index] = (byte)((77 * color.Red + 150 * color.Green + 29 * color.Blue) >> 8);
            red[index] = color.Red;
            blue[index] = color.Blue;
        }
        return new Fingerprint(luma, red, blue);
    }

    public static double Similarity(Fingerprint left, Fingerprint right)
    {
        if (left.Luma.Length != Width * Height || right.Luma.Length != Width * Height)
            throw new ArgumentException("The frame fingerprint has an invalid size.");
        double brightnessDifference = 0;
        double colorDifference = 0;
        double edgeDifference = 0;
        for (var y = 1; y < Height - 1; y++)
        for (var x = 1; x < Width - 1; x++)
        {
            var index = y * Width + x;
            brightnessDifference += Math.Abs(left.Luma[index] - right.Luma[index]);
            colorDifference += Math.Abs(left.Red[index] - right.Red[index]) +
                               Math.Abs(left.Blue[index] - right.Blue[index]);
            edgeDifference += Math.Abs(
                (left.Luma[index] - left.Luma[index - 1]) -
                (right.Luma[index] - right.Luma[index - 1]));
        }
        var count = (Width - 2) * (Height - 2);
        var difference = 0.45 * brightnessDifference / (count * 255) +
                         0.25 * colorDifference / (count * 510) +
                         0.30 * edgeDifference / (count * 255);
        var pixelSimilarity = Math.Clamp(1 - difference, 0, 1);
        var structureSimilarity = SpatialCorrelation(left.Luma, right.Luma, out var lowTexture);
        if (lowTexture)
        {
            // 纯色画面没有可比较的空间结构，改用更严格的像素颜色差判断。
            var meanChannelDifference = (brightnessDifference + colorDifference) / (count * 3);
            return Math.Clamp(1 - meanChannelDifference / 20, 0, 1);
        }
        return 0.5 * pixelSimilarity + 0.5 * structureSimilarity;
    }

    private static double SpatialCorrelation(byte[] left, byte[] right, out bool lowTexture)
    {
        // 忽略边缘，避免台标和黑边主导结果；允许一个像素的位移。
        lowTexture = false;
        var best = 0d;
        for (var shiftY = -1; shiftY <= 1; shiftY++)
        for (var shiftX = -1; shiftX <= 1; shiftX++)
        {
            double sumLeft = 0, sumRight = 0;
            double sumLeftSquared = 0, sumRightSquared = 0, sumProducts = 0;
            const int sampleCount = 26 * 14;
            for (var y = 2; y < Height - 2; y++)
            for (var x = 3; x < Width - 3; x++)
            {
                var first = left[y * Width + x];
                var second = right[(y + shiftY) * Width + x + shiftX];
                sumLeft += first;
                sumRight += second;
                sumLeftSquared += first * first;
                sumRightSquared += second * second;
                sumProducts += first * second;
            }

            var covariance = sumProducts - sumLeft * sumRight / sampleCount;
            var leftVariance = sumLeftSquared - sumLeft * sumLeft / sampleCount;
            var rightVariance = sumRightSquared - sumRight * sumRight / sampleCount;
            if (shiftX == 0 && shiftY == 0)
                lowTexture = leftVariance / sampleCount <= 16 &&
                             rightVariance / sampleCount <= 16;
            var denominator = Math.Sqrt(Math.Max(0, leftVariance) * Math.Max(0, rightVariance));
            if (denominator > 1e-6)
                best = Math.Max(best, covariance / denominator);
        }
        return Math.Clamp(best, 0, 1);
    }

    private sealed class SkiaFramebuffer(SKBitmap bitmap) : ILockedFramebuffer
    {
        public IntPtr Address => bitmap.GetPixels();
        public PixelSize Size => new(bitmap.Width, bitmap.Height);
        public int RowBytes => bitmap.RowBytes;
        public Vector Dpi => new(96, 96);
        public PixelFormat Format => PixelFormat.Bgra8888;
        public void Dispose() { }
    }
}
