using System.Diagnostics;
using TSCutter.GUI.Web;
using Xunit;

namespace TSCutter.GUI.Tests.Application;

public sealed class WebUiStreamFeedbackTests
{
    private static long At(double seconds) => (long)(seconds * Stopwatch.Frequency);
    private static WebUiPlaybackStatus Healthy(uint sequence = 0) => new(sequence, 60, 80, 0, 0, 5, true);

    [Fact]
    public void BrowserReceiptsBoundTheWholeSendPathAndRejectUnknownOrOldFrames()
    {
        var feedback = new WebUiStreamFeedback();
        var stream = new WebUiVideoStream(1, "av1", true);
        feedback.Start(stream);
        for (var i = 0; i < 8; ++i)
        {
            Assert.True(feedback.CanCapture(stream, At(0)));
            feedback.FrameQueued(stream, At(0));
        }
        Assert.False(feedback.CanCapture(stream, At(0)));
        feedback.Report(stream, Healthy(9), At(0.1));
        Assert.False(feedback.CanCapture(stream, At(0.1)));
        feedback.Report(stream, Healthy(3), At(0.2));
        Assert.True(feedback.CanCapture(stream, At(0.2)));
        // 数量尚未满，但画面已过时，仍应暂停继续合成。
        Assert.False(feedback.CanCapture(stream, At(0.5)));
        feedback.Report(stream, Healthy(8), At(0.5));
        Assert.True(feedback.CanCapture(stream, At(0.5)));

        var next = new WebUiVideoStream(2, "h264", true);
        feedback.Start(next);
        feedback.FrameQueued(next, At(1));
        feedback.Report(stream, Healthy(1), At(1.1));
        Assert.False(feedback.CanCapture(next, At(1.5)));
        feedback.Report(next, Healthy(1), At(1.5));
        Assert.True(feedback.CanCapture(next, At(1.5)));
        feedback.FrameQueued(next, At(2));
        feedback.FrameQueued(next, At(2));
        feedback.Report(next, Healthy(2), At(4));
        Assert.False(feedback.HasStalled(next, At(5)));
        feedback.Report(next, Healthy(2), At(6));
        Assert.True(feedback.HasStalled(next, At(7)));
        feedback.Report(next, Healthy(3), At(7));
        Assert.False(feedback.HasStalled(next, At(20)));
    }

    [Fact]
    public void FailedVideoKeepsTheUsersChoiceWhileLegacyClientsCanFallback()
    {
        using var socket = new WebUiVideoSocket();
        socket.Start(1, "h264", feedback: true, manualRecovery: true);
        Assert.NotNull(socket.Stream);
        socket.Stop(1, notify: true, reason: "feedback-timeout");
        Assert.Null(socket.Stream);
        Assert.False(socket.Transport.JpegEnabled);
        Assert.Equal(new WebUiVideoFallback(1, "feedback-timeout"), socket.TakeFallback());
        Assert.Null(socket.TakeFallback());

        var stopped = socket.Transport;
        socket.Start(2, "jpeg", manualRecovery: true);
        Assert.True(socket.Transport.JpegEnabled);
        Assert.NotSame(stopped, socket.Transport);
        var jpeg = socket.Transport;
        socket.Start(3, "jpeg", manualRecovery: true);
        Assert.True(socket.Transport.JpegEnabled);
        Assert.NotSame(jpeg, socket.Transport);

        socket.Start(4, "h264");
        Assert.NotNull(socket.Stream);
        socket.Stop(4, notify: true);
        Assert.True(socket.Transport.JpegEnabled);
    }

    [Fact]
    public void PersistentPressureSlowsCaptureAndStableRecoveryRestoresIt()
    {
        var feedback = new WebUiStreamFeedback();
        var stream = new WebUiVideoStream(1, "av1", true);
        var normal = TimeSpan.FromMilliseconds(25);
        var poor = Healthy() with
        {
            BufferedMs = 400
        };
        feedback.Start(stream);
        feedback.Report(stream, poor, At(0));
        feedback.Report(stream, poor, At(0.2));
        Assert.Equal(normal, feedback.CaptureInterval(normal));
        feedback.Report(stream, poor, At(0.6));
        Assert.True(feedback.CaptureInterval(normal) > normal);
        feedback.Report(stream, Healthy() with
        {
            BufferedMs = double.NaN
        }, At(1.2));
        feedback.Report(stream, poor, At(1.2));
        Assert.True(feedback.CaptureInterval(normal) > TimeSpan.FromMilliseconds(35));
        feedback.Report(stream, Healthy(), At(1.3));
        feedback.Report(stream, Healthy(), At(4.3));
        feedback.Report(stream, Healthy(), At(7.3));
        Assert.Equal(normal, feedback.CaptureInterval(normal));
        var slowDecode = Healthy() with
        {
            DecodeMs = 60
        };
        for (var i = 0; i < 20; ++i)
            feedback.Report(stream, slowDecode, At(8 + i * 0.6));
        Assert.InRange(feedback.CaptureInterval(normal), TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(60));
    }

    [Fact]
    public void HiddenPagesPauseVideoAndResumeWithARefreshWhileLegacyClientsRemainUsable()
    {
        var feedback = new WebUiStreamFeedback();
        var stream = new WebUiVideoStream(1, "av1", true);
        feedback.Start(stream);
        feedback.FrameQueued(stream, At(0));
        feedback.Report(stream, Healthy() with
        {
            Visible = false
        }, At(0));
        Assert.False(feedback.HasStalled(stream, At(30)));
        Assert.False(feedback.CanCapture(stream, At(30)));
        feedback.Report(stream, Healthy(1), At(30));
        Assert.False(feedback.HasStalled(stream, At(30)));
        Assert.True(feedback.CanCapture(stream, At(30)));
        Assert.True(feedback.TakeRefreshRequest());
        Assert.False(feedback.TakeRefreshRequest());

        var legacy = stream with
        {
            Id = 2,
            Feedback = false
        };
        feedback.Start(legacy);
        feedback.Report(legacy, Healthy() with
        {
            Visible = false
        }, At(31));
        for (var i = 0; i < 20; ++i)
            feedback.FrameQueued(legacy, At(31));
        Assert.True(feedback.CanCapture(legacy, At(60)));

        var h264 = stream with
        {
            Id = 3,
            Codec = "h264"
        };
        feedback.Start(h264);
        feedback.FrameQueued(h264, At(60), 64 * 1024);
        feedback.Report(h264, Healthy() with
        {
            Visible = false
        }, At(60));
        feedback.Report(h264, Healthy(1), At(120));
        feedback.Report(h264, Healthy(1), At(120.6));
        Assert.Equal(7_000_000, feedback.VideoBitrate(h264, 7_000_000));
    }

    [Fact]
    public void ByteBudgetBoundsLargeFramesAndIsClearedByAcknowledgementsAndNewStreams()
    {
        var feedback = new WebUiStreamFeedback();
        var stream = new WebUiVideoStream(1, "h264", true);
        feedback.Start(stream);
        for (var index = 0; index < 3; ++index)
        {
            Assert.True(feedback.CanCapture(stream, At(0)));
            feedback.FrameQueued(stream, At(0), 250_000);
        }
        Assert.False(feedback.CanCapture(stream, At(0.05)));
        feedback.Report(stream, Healthy(4), At(0.1));
        Assert.False(feedback.CanCapture(stream, At(0.1)));
        feedback.Report(stream, Healthy(1), At(0.25));
        // 已有回执时，剩下的两帧共 500 KB，超过这条链路当前的 250 KB 预算。
        Assert.False(feedback.CanCapture(stream, At(0.25)));
        feedback.Report(stream, Healthy(3), At(0.3));
        Assert.True(feedback.CanCapture(stream, At(0.3)));
        feedback.Start(stream with
        {
            Id = 2
        });
        Assert.False(feedback.CanCapture(stream, At(0.2)));
        Assert.True(feedback.CanCapture(stream with
        {
            Id = 2
        }, At(0.2)));
    }

    [Fact]
    public void DeliveryRateAfterAnIdleScreenMeasuresOnlyTimeWithFramesInFlight()
    {
        var feedback = new WebUiStreamFeedback();
        var stream = new WebUiVideoStream(1, "h264", true);
        feedback.Start(stream);
        feedback.FrameQueued(stream, At(0), 1000);
        feedback.Report(stream, Healthy(1), At(0.1));
        feedback.FrameQueued(stream, At(5), 200_000);
        feedback.Report(stream, Healthy(2), At(5.1));
        feedback.FrameQueued(stream, At(5.1), 80_000);
        Assert.True(feedback.CanCapture(stream, At(5.1)));
    }

    [Fact]
    public void H264ReducesBitrateBeforeFrameRateAndRestoresItWithoutChangingOtherCodecs()
    {
        var feedback = new WebUiStreamFeedback();
        var stream = new WebUiVideoStream(1, "h264", true);
        var interval = TimeSpan.FromSeconds(1.0 / 60);
        var poor = Healthy() with
        {
            PresentMs = 400
        };
        feedback.Start(stream);
        feedback.Report(stream, poor, At(0));
        feedback.Report(stream, poor, At(0.6));
        Assert.Equal(5_600_000, feedback.VideoBitrate(stream, 7_000_000));
        Assert.Equal(interval, feedback.CaptureInterval(interval));
        feedback.Report(stream, Healthy(), At(1));
        feedback.Report(stream, Healthy(), At(4.1));
        Assert.Equal(7_000_000, feedback.VideoBitrate(stream, 7_000_000));
        for (var index = 0; index < 14; ++index)
            feedback.Report(stream, poor, At(5 + index * 0.6));
        Assert.Equal(1_400_000, feedback.VideoBitrate(stream, 7_000_000));
        Assert.True(feedback.CaptureInterval(interval) > interval);

        var av1 = stream with
        {
            Id = 2,
            Codec = "av1"
        };
        feedback.Start(av1);
        feedback.Report(av1, poor, At(20));
        feedback.Report(av1, poor, At(20.6));
        Assert.Equal(7_000_000, feedback.VideoBitrate(av1, 7_000_000));
        Assert.True(feedback.CaptureInterval(interval) > interval);
    }
}
