using System;
using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace TSCutter.GUI.Services.Application.SystemProgress;

/// <summary>绘制叠加在 Dock 图标上的进度条图片。</summary>
internal static class DockProgressBarRenderer
{
    /// <summary>绘制只含进度条、其余透明的图片。</summary>
    public static byte[] Render(SystemProgressSnapshot snapshot, int imageSize)
    {
        var left = imageSize * 0.12;
        var width = imageSize * 0.76;
        var height = imageSize * 0.085;
        var top = imageSize * 0.80;
        var radius = height / 2;
        var track = new Rect(left, top, width, height);

        using var bitmap = new RenderTargetBitmap(new PixelSize(imageSize, imageSize), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
                new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 2), track, radius, radius);

            var fillColor = snapshot.State switch
            {
                SystemProgressState.Error => Color.FromRgb(255, 69, 58),
                SystemProgressState.Paused => Color.FromRgb(255, 204, 0),
                _ => Color.FromRgb(10, 132, 255),
            };
            var inner = track.Deflate(3);
            if (snapshot.State == SystemProgressState.Indeterminate)
            {
                // 不确定进度画成整条斜纹，不做动画。
                using (context.PushClip(new RoundedRect(inner, inner.Height / 2)))
                {
                    context.FillRectangle(new SolidColorBrush(Color.FromArgb(150, fillColor.R, fillColor.G, fillColor.B)), inner);
                    var stripe = new SolidColorBrush(fillColor);
                    for (var x = inner.X - inner.Height; x < inner.Right; x += inner.Height * 1.4)
                    {
                        var geometry = new PolylineGeometry(
                        [
                            new Point(x, inner.Bottom), new Point(x + inner.Height * 0.7, inner.Bottom),
                            new Point(x + inner.Height * 1.4, inner.Y), new Point(x + inner.Height * 0.7, inner.Y),
                        ], isFilled: true);
                        context.DrawGeometry(stripe, null, geometry);
                    }
                }
            }
            else
            {
                var fillWidth = Math.Max(inner.Height, inner.Width * Math.Clamp(snapshot.Value, 0, 1));
                var fill = new Rect(inner.X, inner.Y, fillWidth, inner.Height);
                context.DrawRectangle(new SolidColorBrush(fillColor), null, fill, inner.Height / 2, inner.Height / 2);
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return stream.ToArray();
    }
}
