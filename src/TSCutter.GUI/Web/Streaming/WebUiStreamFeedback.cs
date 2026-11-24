using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TSCutter.GUI.Web;

internal readonly record struct WebUiPlaybackStatus(uint Sequence, double PresentMs, double BufferedMs,
    int Pending, double QueuedMs, double DecodeMs, bool Visible);

internal sealed class WebUiStreamFeedback
{
    private readonly object gate = new();
    private static readonly Histogram<double> TargetBitrate = WebUiSession.FrameMeter.CreateHistogram<double>("webui.video.target.bitrate", "bit/s");
    private readonly Queue<(uint Sequence, long Created, int Bytes)> outstanding = new();
    private long outstandingBytes;
    private double deliveryBytesPerSecond;
    private double receiptMs;
    private long? lastDelivery;
    private double bitrateScale = 1;
    private WebUiVideoStream? active;
    private uint sequence;
    private uint acknowledged;
    private long lastProgress;
    private bool visible = true;
    private bool refresh;
    private double intervalScale = 1;
    private long? poorSince;
    private long? goodSince;

    internal void Start(WebUiVideoStream? stream)
    {
        lock (gate)
        {
            active = stream;
            outstanding.Clear();
            outstandingBytes = 0;
            deliveryBytesPerSecond = receiptMs = 0;
            lastDelivery = null;
            bitrateScale = 1;
            sequence = acknowledged = 0;
            lastProgress = 0;
            visible = true;
            refresh = false;
            intervalScale = 1;
            poorSince = goodSince = null;
        }
    }

    internal bool CanCapture(WebUiVideoStream stream, long now)
    {
        lock (gate)
        {
            if (active != stream)
                return false;
            if (!stream.Feedback)
                return true;
            // 三帧余量供 MSE 首次启动；其余在途数据按实测交付速率限制，避免大关键帧塞满链路。
            var byteBudget = deliveryBytesPerSecond > 0
                ? Math.Clamp(deliveryBytesPerSecond * 0.25, 64 * 1024, 1024 * 1024) : 512 * 1024;
            var priming = acknowledged == 0 && outstanding.Count < 3;
            return visible && (priming || outstandingBytes < byteBudget) &&
                outstanding.Count < Math.Clamp((stream.FrameRate + 3) / 4, 6, 16) &&
                (outstanding.Count == 0 || Stopwatch.GetElapsedTime(outstanding.Peek().Created, now) < TimeSpan.FromMilliseconds(400));
        }
    }

    internal bool HasStalled(WebUiVideoStream stream, long now)
    {
        lock (gate)
            return active == stream && stream.Feedback && visible && outstanding.Count > 0 &&
                Stopwatch.GetElapsedTime(lastProgress, now) >= TimeSpan.FromSeconds(3);
    }

    internal uint FrameQueued(WebUiVideoStream stream, long now, int bytes = 0)
    {
        lock (gate)
        {
            if (active != stream || !stream.Feedback)
                return 0;
            if (outstanding.Count == 0)
                lastProgress = now;
            var next = unchecked(++sequence);
            bytes = Math.Max(0, bytes);
            outstanding.Enqueue((next, now, bytes));
            outstandingBytes += bytes;
            return next;
        }
    }

    internal TimeSpan CaptureInterval(TimeSpan normal)
    {
        lock (gate)
            return TimeSpan.FromTicks((long)Math.Min(TimeSpan.FromMilliseconds(125).Ticks, normal.Ticks * intervalScale));
    }

    internal int VideoBitrate(WebUiVideoStream stream, int selectedBitrate)
    {
        lock (gate)
        {
            var bitrate = active == stream && stream is { IsH264: true, Feedback: true }
                ? Math.Min(selectedBitrate, Math.Max(500_000, (int)(selectedBitrate * bitrateScale))) : selectedBitrate;
            if (TargetBitrate.Enabled)
                TargetBitrate.Record(bitrate);
            return bitrate;
        }
    }

    internal bool TakeRefreshRequest()
    {
        lock (gate)
        {
            var requested = refresh;
            refresh = false;
            return requested;
        }
    }

    internal void Report(WebUiVideoStream stream, WebUiPlaybackStatus report, long now)
    {
        if (!Valid(report.PresentMs) || !Valid(report.BufferedMs) || !Valid(report.QueuedMs) || !Valid(report.DecodeMs) ||
            report.Pending is < 0 or > 90)
            return;
        lock (gate)
        {
            if (active != stream || !stream.Feedback)
                return;
            // 只接受仍在途的帧或上次回执；旧流、倒序或超前的回执不能释放发送预算。
            if (report.Sequence != acknowledged)
            {
                var found = false;
                foreach (var frame in outstanding)
                    if (frame.Sequence == report.Sequence)
                    {
                        found = true;
                        break;
                    }
                if (!found)
                    return;
                var oldestCreated = outstanding.Peek().Created;
                // 静止画面没有在途数据，空闲时间不能算作下一批帧的传输用时。
                var deliveryStart = Math.Max(lastDelivery ?? oldestCreated, oldestCreated);
                long deliveredBytes = 0;
                (uint Sequence, long Created, int Bytes) delivered;
                do
                {
                    delivered = outstanding.Dequeue();
                    deliveredBytes += delivered.Bytes;
                } while (delivered.Sequence != report.Sequence);
                outstandingBytes -= deliveredBytes;
                var elapsed = Stopwatch.GetElapsedTime(deliveryStart, now).TotalSeconds;
                if (deliveredBytes > 0 && elapsed is >= 0.01 and <= 3)
                {
                    var rate = deliveredBytes / elapsed;
                    deliveryBytesPerSecond = deliveryBytesPerSecond == 0 ? rate : deliveryBytesPerSecond * 0.8 + rate * 0.2;
                }
                var receipt = Math.Max(0, Stopwatch.GetElapsedTime(delivered.Created, now).TotalMilliseconds);
                receiptMs = lastDelivery is null ? receipt : receiptMs * 0.8 + receipt * 0.2;
                lastDelivery = now;
                acknowledged = report.Sequence;
                lastProgress = now;
            }
            if (report.Visible != visible)
            {
                visible = report.Visible;
                refresh = visible;
                if (visible)
                    lastProgress = now;
                // 后台停留时间不能进入链路延迟估计，恢复后的第一批回执重新计时。
                receiptMs = 0;
                lastDelivery = null;
                intervalScale = 1;
                poorSince = goodSince = null;
            }
            if (!visible)
                return;
            // 按当前产帧间隔衡量解码，避免慢设备在已经降频后仍被一路压到最低帧率。
            var decodeBudget = Math.Max(20, 1000.0 / stream.FrameRate * intervalScale);
            var recentReceipt = lastDelivery is { } delivery && Stopwatch.GetElapsedTime(delivery, now) < TimeSpan.FromSeconds(1);
            var networkQueued = recentReceipt && receiptMs > 250 || outstanding.Count > 0 &&
                Stopwatch.GetElapsedTime(outstanding.Peek().Created, now) > TimeSpan.FromMilliseconds(250);
            var processingPressure = report.BufferedMs > 240 || report.Pending > 2 || report.QueuedMs > 120 ||
                report.DecodeMs > decodeBudget * 1.5;
            var bandwidthPressure = networkQueued || report.PresentMs > 280;
            var poor = bandwidthPressure || processingPressure;
            var good = !networkQueued && report.BufferedMs < 200 && report.Pending <= 1 && report.QueuedMs < 40 &&
                report.PresentMs < 220 && report.DecodeMs < decodeBudget * 1.1;
            if (poor)
            {
                goodSince = null;
                poorSince ??= now;
                if (Stopwatch.GetElapsedTime(poorSince.Value, now) >= TimeSpan.FromMilliseconds(500))
                {
                    // H.264 链路积压时优先降码率；处理速度不足或已到码率下限时才进一步降采集频率。
                    var canReduceBitrate = stream.IsH264 && bandwidthPressure && bitrateScale > 0.2;
                    if (canReduceBitrate)
                        bitrateScale = Math.Max(0.2, bitrateScale * 0.8);
                    if (!canReduceBitrate || processingPressure)
                        intervalScale = Math.Min(8, intervalScale * 1.3);
                    poorSince = now;
                }
            }
            else if (good)
            {
                poorSince = null;
                goodSince ??= now;
                if (Stopwatch.GetElapsedTime(goodSince.Value, now) >= TimeSpan.FromSeconds(3))
                {
                    intervalScale = Math.Max(1, intervalScale / 1.3);
                    bitrateScale = Math.Min(1, bitrateScale / 0.8);
                    goodSince = now;
                }
            }
            else
                poorSince = goodSince = null;
        }
    }

    private static bool Valid(double number) => double.IsFinite(number) && number is >= 0 and <= 60_000;
}
