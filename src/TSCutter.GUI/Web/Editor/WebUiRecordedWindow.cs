using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using Avalonia;
using SkiaSharp;

namespace TSCutter.GUI.Web;

internal sealed class WebUiRecordedWindow
{
    private readonly object gate = new();
    private sealed record PendingPicture(SKPicture Picture, PixelSize Size, Rect[]? Replacement);
    private static readonly Counter<long> CoalescedPictures = WebUiSession.FrameMeter.CreateCounter<long>("webui.render.coalesced.pictures");
    private readonly List<PendingPicture> pending = [];
    private long pendingSince;
    private static readonly TimeSpan MaxPendingAge = TimeSpan.FromMilliseconds(250);
    internal PixelSize Size { get; private set; } = new(1, 1);
    internal bool NeedsRefresh { get; private set; } = true;

    internal void Publish(SKPicture picture, PixelSize size, Rect[]? replacement = null)
    {
        lock (gate)
        {
            Size = size;
            var now = Stopwatch.GetTimestamp();
            // 即使同一区域一直被合并，后台或拥塞期间也要停止位图快照；恢复后再补完整画面。
            if (pendingSince != 0 && Stopwatch.GetElapsedTime(pendingSince, now) >= MaxPendingAge)
                Reset();
            if (NeedsRefresh)
            {
                picture.Dispose();
                return;
            }
            if (pendingSince == 0)
                pendingSince = now;
            if (replacement is { Length: > 0 })
            {
                // 只有完整清除并重画的区域才能替换旧指令，不能用脏区外包矩形填平区域间的空隙。
                var fullWindow = new Rect(size.ToSize(1));
                for (var index = pending.Count - 1; index >= 0; --index)
                {
                    var older = pending[index];
                    if (older.Size != size || !(older.Replacement is { } regions
                        ? regions.All(region => replacement.Any(area => area.Contains(region)))
                        : replacement.Any(area => area.Contains(fullWindow))))
                        continue;
                    older.Picture.Dispose();
                    pending.RemoveAt(index);
                    CoalescedPictures.Add(1);
                }
            }
            // 合并之后仍然过多，说明有不同区域的更新积压，必须重新请求完整画面。
            if (pending.Count >= 4)
            {
                Reset();
                picture.Dispose();
                return;
            }
            pending.Add(new PendingPicture(picture, size, replacement));
            WebUiRenderBackend.InvalidateScene();
        }
    }

    internal void BeginRefresh()
    {
        lock (gate)
        {
            Reset();
            NeedsRefresh = false;
        }
    }

    internal SKPicture[] TakePictures() => TakePictures(out _);

    internal SKPicture[] TakePictures(out Rect? damage)
    {
        lock (gate)
        {
            // 替换区域也是本次绘图的可靠外层裁剪；未知裁剪必须保守地更新整个窗口。
            damage = null;
            if (pending.Count > 0 && pending.All(item => item.Replacement is { Length: > 0 }))
                foreach (var item in pending)
                    foreach (var region in item.Replacement!)
                        damage = damage?.Union(region) ?? region;
            var result = pending.Select(item => item.Picture).ToArray();
            pending.Clear();
            pendingSince = 0;
            return result;
        }
    }

    internal void Reset()
    {
        lock (gate)
        {
            foreach (var item in pending)
                item.Picture.Dispose();
            pending.Clear();
            pendingSince = 0;
            NeedsRefresh = true;
        }
    }
}

internal sealed record WebUiWindowFrame(WebUiRecordedWindow Window, SKPicture[] Pictures,
    PixelSize Size, SKPoint Offset, SKPoint Scale, Rect? Damage = null);
