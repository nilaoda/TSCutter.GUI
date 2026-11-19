using System;
using System.Collections.Generic;
using System.Threading;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.Extensions;
using TSCutter.GUI.FFmpeg;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Models.Preview;

// 逐帧步进的帧缓存与顺序续解。逐帧定位每次都要从关键帧重新解码到目标；这里保留最近解出的画面，
// 前进时若解码器与读取位置仍停在上次输出之后就接着送包，后退时直接取缓存中的上一帧。
public partial class VideoInstance
{
    /// <summary>软解画面的缓存字节上限：4K 10 位约 10 帧，1080p 8 位达到帧数上限。</summary>
    internal const long StepCacheByteLimit = 256L * 1024 * 1024;
    internal const int StepCacheFrameLimit = 32;

    /// <summary>
    /// 硬解画面占用解码器的表面池（D3D11 的池大小固定），只保留少量引用，不影响后续解码。
    /// </summary>
    internal const int StepCacheHardwareFrameLimit = 4;

    private readonly List<StepFrame> stepFrames = [];
    private long stepFrameBytes;
    private bool stepCaching;
    private bool stepOperation;
    private StepContinuation? stepContinuation;

    /// <summary>
    /// 显示的逐帧画面取自缓存时，读取位置不在它的包之后；记下它的 PTS，依赖读取位置的关键帧导航前再恢复。
    /// 任何定位都会重新确定读取位置，同时清除此标记。
    /// </summary>
    private long? stepReadPositionPts;

    private sealed record StepFrame(long Pts, Frame Frame, long Bytes);

    /// <summary>解码器与读取位置停在缓存最后一帧之后，可直接继续送包。</summary>
    private sealed record StepContinuation(CodecContext Decoder, long LastPts);

    /// <summary>缓存中的帧数（测试用）。</summary>
    internal int StepCacheCount => stepFrames.Count;

    /// <summary>逐帧缓存只在主预览启用：文件和码流稳定，其他实例都是短时扫描。</summary>
    private bool StepCacheEnabled => IsMainPreview && Disc is null;

    /// <summary>逐帧步进的读取操作：不先结束续解状态，其余读取操作开始前都会恢复关键帧导航的解码器状态。</summary>
    private T RunStepOperation<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        lock (decoderSync)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            var previous = stepOperation;
            stepOperation = true;
            try { return RunReadOperationCore(operation, cancellationToken, null); }
            finally { stepOperation = previous; }
        }
    }

    /// <summary>
    /// 结束续解：清空保留了输出的解码器，之后的关键帧导航从当前读取位置照常进行（与步进后立即清空的效果相同）。
    /// </summary>
    private void EndStepContinuation()
    {
        if (Interlocked.Exchange(ref stepContinuation, null) is not { } continuation)
            return;
        if (!continuation.Decoder.IsClosed && !continuation.Decoder.IsInvalid)
            continuation.Decoder.FlushBuffers();
    }

    private void ClearStepCache()
    {
        stepContinuation = null;
        foreach (var frame in stepFrames)
            frame.Frame.Dispose();
        stepFrames.Clear();
        stepFrameBytes = 0;
    }

    /// <summary>新的一轮定位解码：之前的缓存与本轮画面不一定相连，全部丢弃。</summary>
    private void BeginStepRun()
    {
        EndStepContinuation();
        ClearStepCache();
    }

    /// <summary>
    /// 按显示顺序缓存一帧。同一轮解码的输出是连续的，超出上限时丢弃最早的画面；
    /// 已缓存的时间戳不重复保存（解码器不会输出时间戳倒退的画面，出现即说明跨轮，清空后重新开始）。
    /// </summary>
    private void CacheStepFrame(Frame frame, long pts)
    {
        if (!stepCaching || pts == FF.AV_NOPTS_VALUE)
            return;
        if (stepFrames.Count > 0 && pts <= stepFrames[^1].Pts)
        {
            if (stepFrames.Exists(item => item.Pts == pts))
                return;
            ClearStepCache();
        }
        var hardware = frame.HasHardwareFramesContext;
        var bytes = hardware ? 0 : EstimateFrameBytes(frame);
        stepFrames.Add(new StepFrame(pts, frame.CloneReference(), bytes));
        stepFrameBytes += bytes;
        var frameLimit = hardware ? StepCacheHardwareFrameLimit : StepCacheFrameLimit;
        while (stepFrames.Count > 1 && (stepFrames.Count > frameLimit || stepFrameBytes > StepCacheByteLimit))
        {
            stepFrameBytes -= stepFrames[0].Bytes;
            stepFrames[0].Frame.Dispose();
            stepFrames.RemoveAt(0);
        }
    }

    private static unsafe long EstimateFrameBytes(Frame frame)
    {
        AVFrame* raw = frame;
        var descriptor = FF.av_pix_fmt_desc_get((AVPixelFormat)raw->format);
        long total = 0;
        for (uint plane = 0; plane < 4; plane++)
        {
            if (raw->data[plane] == null)
                continue;
            var height = plane is 1 or 2 && descriptor != null
                ? (raw->height + (1 << descriptor->log2_chroma_h) - 1) >> descriptor->log2_chroma_h
                : raw->height;
            total += (long)Math.Abs(raw->linesize[plane]) * height;
        }
        return total;
    }

    /// <summary>缓存中紧接在 anchor 之后（direction &gt; 0）或之前的画面；anchor 本身必须在缓存中，才能保证中间没有缺帧。</summary>
    private StepFrame? FindCachedStep(long anchorPts, int direction)
    {
        var index = stepFrames.FindIndex(item => item.Pts == anchorPts);
        if (index < 0)
            return null;
        var next = index + Math.Sign(direction);
        return next >= 0 && next < stepFrames.Count ? stepFrames[next] : null;
    }

    /// <summary>
    /// 从缓存或续解取得相邻帧并呈现；都不可用时返回 false，由调用方从关键帧定位解码。
    /// 后退命中的画面早于时间轴起点时与原逻辑一致，视为片头没有上一帧。
    /// </summary>
    private bool TryStepFromCache(long anchorPts, int direction, CancellationToken token, out DecodeResult? result)
    {
        result = null;
        if (!StepCacheEnabled)
            return false;
        var cached = FindCachedStep(anchorPts, direction);
        if (cached is null && direction > 0 && stepContinuation is { } continuation
            && continuation.LastPts == anchorPts && stepFrames.Count > 0 && stepFrames[^1].Pts == anchorPts)
        {
            // 续解送包到这一帧为止，读取位置与重新定位解码时相同。
            cached = WithFullDecoder(() => ContinueStepDecode(continuation, anchorPts, token));
            stepReadPositionPts = null;
        }
        else if (cached is not null)
            stepReadPositionPts = cached.Pts;
        if (cached is null)
            return false;
        if (direction < 0 && cached.Pts < firstFrameTimestamp)
            return true;
        result = PresentStepFrame(cached.Frame, cached.Pts, token);
        return result is not null;
    }

    /// <summary>
    /// 与不缓存时一致：从关键帧精确解码到最后显示的逐帧画面，读取位置停在它的包之后，关键帧导航由此继续。
    /// </summary>
    private void RestoreStepReadPosition(CancellationToken token)
    {
        if (stepReadPositionPts is not { } pts)
            return;
        stepReadPositionPts = null;
        var restored = DecodeMatchingFrame(pts, value => value == pts, pts, token);
        if (restored is null)
            return;
        restored.BitmapLease?.Dispose();
        if (restored.BitmapLease is null)
            restored.Bitmap?.Dispose();
        restored.GpuFrame?.Dispose();
    }

    /// <summary>
    /// 接着上次的解码状态送包，直到输出晚于 anchor 的画面。解码器不是上次的那个（硬解回退等）时放弃续解。
    /// </summary>
    private StepFrame? ContinueStepDecode(StepContinuation continuation, long anchorPts, CancellationToken token)
    {
        if (!ReferenceEquals(videoDecoder, continuation.Decoder))
        {
            EndStepContinuation();
            return null;
        }
        stepContinuation = null;
        stepCaching = true;
        exactFrameFilter = static _ => false;
        SetKeyFramesOnly(false);
        var completed = false;
        try
        {
            foreach (var packet in inFc.ReadPackets())
            {
                token.ThrowIfCancellationRequested();
                activeReadDeadline?.ThrowIfInterrupted();
                if (packet.StreamIndex != videoStreamIndex)
                    continue;
                DecodePacket(packet, token, 0, 0, preserveRemainingOutput: true);
                if (FindCachedStep(anchorPts, 1) is { } next)
                {
                    completed = true;
                    return next;
                }
            }
            // 文件尾：取出解码器里延迟输出的画面。
            DecodePacket(null, token, 0, 0, preserveRemainingOutput: true);
            DecodePacket(null, token, 0, 0, receiveOnly: true, preserveRemainingOutput: true);
            return FindCachedStep(anchorPts, 1);
        }
        finally
        {
            exactFrameFilter = null;
            stepCaching = false;
            SetKeyFramesOnly(true);
            if (completed && stepFrames.Count > 0)
                stepContinuation = new StepContinuation(videoDecoder, stepFrames[^1].Pts);
            else
                videoDecoder.FlushBuffers();
        }
    }

    /// <summary>
    /// 定位解码命中后，把同一次送包已产生、但排在目标之后的输出也放进缓存，解码器状态与读取位置才能对应到缓存末尾。
    /// </summary>
    private void DrainStepOutput(CancellationToken token)
    {
        var filter = exactFrameFilter;
        exactFrameFilter = static _ => false;
        try { DecodePacket(null, token, 0, 0, receiveOnly: true, preserveRemainingOutput: true); }
        finally { exactFrameFilter = filter; }
    }

    /// <summary>与 DecodePacket 相同的方式呈现缓存中的画面，并更新当前位置和保留帧。</summary>
    private DecodeResult? PresentStepFrame(Frame cached, long pts, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var frame = cached.CloneReference();
        CompleteFrameColorMetadata(frame);
        var pktPosition = frame.PacketPosition;
        currentKeyFramePts = pts;
        PositionInFile = CanBinaryClip && PacketLayout.IsM2ts && pktPosition >= 0 ? PacketLayout.AlignPosition(pktPosition) : pktPosition;
        var isHardwareFrame = frame.HasHardwareFramesContext;
        var displayGeometry = GetDisplayGeometry(frame);
        var range = GetVideoDynamicRange(frame);
        var pts90k = pts == FF.AV_NOPTS_VALUE ? FF.AV_NOPTS_VALUE
            : (long)Math.Round(pts * (double)timeBase.num * 90_000 / timeBase.den);
        if (TryCreatePreviewGpuFrame(frame, 0, 0, out var preview, out var mode))
        {
            IsHardwareDecoding = isHardwareFrame;
            IsGpuPresentation = true;
            PresentationMode = mode;
            var gpuResult = new DecodeResult
            {
                GpuFrame = preview, SourcePixelSize = displayGeometry.PixelSize,
                RequiresSampleAspectRatioCorrection = displayGeometry.RequiresCorrection, VideoDynamicRange = range,
                FrameTimestamp = PtsToTimeSpan(pts), FramePts = pts, FramePts90k = pts90k, FramePosition = PositionInFile,
                FrameEndSignature = FindKeyPacketBoundary(pktPosition, pts), PresentationMode = mode,
            };
            try
            {
                RetainPreviewFrame(frame, gpuResult, retainStandardFrame: true);
                return gpuResult;
            }
            catch
            {
                preview!.Dispose();
                throw;
            }
        }
        // GPU 不可用：硬件帧先回读，再在 CPU 上转换成位图。
        Frame? software = null;
        try
        {
            if (isHardwareFrame)
            {
                try { software = frame.TransferToSoftwareFrame(); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    throw new HardwareDecodeException(exception, pts, allowFullPacketRetry: false);
                }
            }
            IsHardwareDecoding = isHardwareFrame;
            IsGpuPresentation = false;
            PresentationMode = isHardwareFrame ? VideoPresentationMode.HardwareCpuTransfer : VideoPresentationMode.SoftwareBitmap;
            var bitmap = ConvertPreviewFrame(software ?? frame, 0, 0, out var lease);
            var result = new DecodeResult
            {
                Bitmap = bitmap, BitmapLease = lease, SourcePixelSize = displayGeometry.PixelSize,
                RequiresSampleAspectRatioCorrection = displayGeometry.RequiresCorrection, VideoDynamicRange = range,
                FrameTimestamp = PtsToTimeSpan(pts), FramePts = pts, FramePts90k = pts90k, FramePosition = PositionInFile,
                FrameEndSignature = FindKeyPacketBoundary(pktPosition, pts), PresentationMode = PresentationMode,
            };
            try
            {
                RetainPreviewFrame(software ?? frame, result);
                return result;
            }
            catch
            {
                if (lease is not null) lease.Dispose(); else bitmap.Dispose();
                throw;
            }
        }
        finally
        {
            software?.Dispose();
        }
    }
}
