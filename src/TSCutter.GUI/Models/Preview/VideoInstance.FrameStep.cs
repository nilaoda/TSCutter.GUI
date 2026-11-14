using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.Extensions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Utils.Discs;
using TSCutter.GUI.Utils.Media;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Models.Preview;

// 帧精确剪辑的逐帧定位。关键帧导航只解码关键包；这里从不晚于目标的关键帧送入完整码流，
// 解码输出按显示顺序排列，只转换目标画面。
public partial class VideoInstance
{
    private Func<long, bool>? exactFrameFilter;

    /// <summary>
    /// 完整解码（逐帧定位、按时间精确定位、智能剪辑导航条的逐帧窗口）用的多线程软解码器，首次需要时创建。
    /// 关键帧导航每次只解一个关键帧，帧线程既不能加速、又会延迟输出，仍用单线程的 <see cref="videoDecoder"/>；
    /// 完整解码连续送包并在结束时排空，帧线程的输出延迟不影响结果。
    /// </summary>
    private CodecContext? fullDecoder;
    private bool fullDecoderUnavailable;

    /// <summary>
    /// 完整解码的线程数，不超过 6。4K50 HEVC 10 位实测导航条逐帧窗口（9 帧）：单线程 744 ms，4 线程 374 ms，
    /// 6 线程 290 ms（约 2.6 倍）、内存多约 220 MB，8 线程 271 ms、内存多约 350 MB——8 线程收益很小，内存与 CPU 争抢却明显增加。
    /// </summary>
    internal static int FullDecodeThreadCount => Math.Clamp(Environment.ProcessorCount, 1, 6);

    /// <summary>当前是否由多线程解码器完整解码（测试用）。</summary>
    internal bool UsesFullDecoder => fullDecoder is not null && ReferenceEquals(videoDecoder, fullDecoder);

    /// <summary>
    /// 完整解码期间把单线程软解码器换成多线程解码器，结束后换回。多线程解码器的状态保留到下次，
    /// 导航条逐帧前进时可接着顺序解码；关键帧解码器在文件位置移动后清空残留输出。
    /// 硬解与逐行刷新素材（已是自动线程的完整解码）不替换；创建失败时沿用原解码器。
    /// </summary>
    private T WithFullDecoder<T>(Func<T> action)
    {
        if (hardwareDecoderOpened || useFullPacketDecoding && UsesGradualRefresh || UsesFullDecoder ||
            (fullDecoder ??= OpenFullDecoder()) is not { } decoder)
            return action();
        var keyFrameDecoder = videoDecoder;
        videoDecoder = decoder;
        try
        {
            return action();
        }
        finally
        {
            decoder.SkipFrame = AVDiscard.AVDISCARD_DEFAULT;
            videoDecoder = keyFrameDecoder;
            keyFrameDecoder.FlushBuffers();
        }
    }

    private CodecContext? OpenFullDecoder()
    {
        if (fullDecoderUnavailable)
            return null;
        foreach (var codec in SoftwareDecoderCandidates)
        {
            CodecContext? candidate = null;
            try
            {
                candidate = new CodecContext(codec);
                candidate.FillParameters(inVideoStream.Codecpar!);
                candidate.PacketTimeBase = inVideoStream.TimeBase;
                candidate.ThreadCount = FullDecodeThreadCount;
                candidate.Open();
                Console.WriteLine($"Full decoding software decoder opened: {codec.Name} ({FullDecodeThreadCount} threads)");
                var opened = candidate;
                candidate = null;
                return opened;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Failed to open full decoding decoder {codec.Name}: {exception.Message}");
            }
            finally
            {
                candidate?.Close();
                candidate?.Dispose();
            }
        }
        fullDecoderUnavailable = true;
        return null;
    }

    private void CloseFullDecoder()
    {
        fullDecoder?.Close();
        fullDecoder?.Dispose();
        fullDecoder = null;
    }

    public FrameAccurateSupport FrameAccurateSupport { get; private set; } = FrameAccurateSupport.Container;

    /// <summary>时间轴原点（首个可显示画面）的 PTS；片段从文件开头开始时以此为起点。</summary>
    internal long FirstFramePts => firstFrameTimestamp;

    /// <summary>
    /// 帧精确导出的轨道：MP4/MKV 沿用封装导出的轨道选择；TS 取预览视频所在节目的音视频和字幕，
    /// 去掉 <paramref name="excludedPids"/> 中未选的轨道（预览视频除外）。
    /// </summary>
    internal FrameAccurateExportSettings CreateFrameAccurateSettings(RemuxExportSettings? remux,
        IReadOnlyCollection<int>? excludedPids = null)
    {
        var container = remux?.Container ?? PacketLayout.Extension;
        var owner = inFc.Programs.Where(program => program.ContainsStream(videoStreamIndex)).Take(1).ToArray();
        IEnumerable<MediaStream> candidates = remux is not null
            ? remux.StreamIndices.Where(index => index < inFc.Streams.Count).Select(index => inFc.Streams[index])
            : owner.Length > 0 ? inFc.Streams.Where(stream => owner[0].ContainsStream(stream.Index)) : inFc.Streams;
        var ts = container is "ts" or "m2ts";
        // 数据轨道（如 SCTE-35）和容器无法表达的字幕不输出。
        var indices = candidates.Where(stream => stream.Codecpar?.CodecType switch
        {
            AVMediaType.AVMEDIA_TYPE_VIDEO => stream.Index == videoStreamIndex || stream.IsAttachedPicture && !ts,
            AVMediaType.AVMEDIA_TYPE_AUDIO => true,
            AVMediaType.AVMEDIA_TYPE_SUBTITLE => FrameAccurateExportService.IsTextSubtitle(stream.Codecpar.CodecId) && !ts ||
                ts && stream.Codecpar.CodecId is AVCodecID.AV_CODEC_ID_DVB_SUBTITLE or AVCodecID.AV_CODEC_ID_DVB_TELETEXT ||
                FrameAccurateExportService.IsDiscSubtitle(Disc, container, stream.Codecpar.CodecId),
            AVMediaType.AVMEDIA_TYPE_ATTACHMENT => container == "mkv",
            _ => false
        } && (remux is not null || excludedPids?.Contains(stream.Id) != true)).Select(stream => stream.Index).ToList();
        if (!indices.Contains(videoStreamIndex))
            indices.Insert(0, videoStreamIndex);
        return new FrameAccurateExportSettings
        {
            Container = container, VideoIndex = videoStreamIndex, StreamIndices = indices, Disc = Disc
        };
    }

    /// <summary>
    /// 解码与锚点相邻的上一帧或下一帧；片头没有上一帧时返回 null。
    /// DVD 的当前画面可能来自播放（另一条读取的时间轴），按画面锚点换算到本预览的时间轴。
    /// </summary>
    internal Task<DecodeResult?> DecodeAdjacentFrameAsync(long anchorPts, int direction, DvdFrameAnchor frameAnchor = default,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => RunReadOperation(() => DecodeAdjacentFrame(ResolveDvdFramePts(anchorPts, frameAnchor, cancellationToken),
            direction, cancellationToken), cancellationToken), cancellationToken);

    /// <summary>
    /// DVD 画面锚点在本预览时间轴上的 PTS；锚点关键帧还没在本预览中读到时，先在附近读一遍包登记关键帧。
    /// 非 DVD、锚点无效或附近找不到该关键帧时沿用原 PTS。
    /// </summary>
    private long ResolveDvdFramePts(long pts, DvdFrameAnchor anchor, CancellationToken token)
    {
        if (!anchor.IsValid || inFc is not InterruptibleInputFormatContext { DvdTimeline: { } timeline })
            return pts;
        if (!timeline.TryGetKeyFramePts(anchor.Key, out var key))
        {
            var gap = Math.Max(1, keyFrameGap);
            var approximate = pts - anchor.Offset;
            Seek(approximate - gap * 2, AVSEEK_FLAG.Backward);
            foreach (var packet in inFc.ReadPackets())
            {
                token.ThrowIfCancellationRequested();
                activeReadDeadline?.ThrowIfInterrupted();
                if (packet.StreamIndex == videoStreamIndex && packet.Pts != FF.AV_NOPTS_VALUE && packet.Pts > approximate + gap * 4)
                    break;
            }
            if (!timeline.TryGetKeyFramePts(anchor.Key, out key))
                return pts;
        }
        return key + anchor.Offset;
    }

    /// <summary>
    /// 按时间偏移定位：精确解码显示时间不早于 targetPts 的第一帧（目标早于首帧时取首帧）；
    /// 目标超出最后一帧时取最后一帧。
    /// </summary>
    internal Task<DecodeResult?> DecodeFrameFromPtsAsync(long targetPts, CancellationToken cancellationToken = default) =>
        Task.Run(() => RunReadOperation(() => WithSoftwareFallback(() =>
        {
            var target = Math.Clamp(targetPts, firstFrameTimestamp, Math.Max(firstFrameTimestamp, maxPts - 1));
            return DecodeMatchingFrame(target, pts => pts >= target, stopAfter: long.MaxValue, cancellationToken)
                   ?? DecodeAdjacentFrameCore(target, -1, cancellationToken);
        }, cancellationToken), cancellationToken), cancellationToken);

    private DecodeResult? DecodeAdjacentFrame(long anchorPts, int direction, CancellationToken token) =>
        WithSoftwareFallback(() => DecodeAdjacentFrameCore(anchorPts, direction, token), token);

    /// <summary>逐帧解码时硬解失败：改用软解后重试一次，之后的导航都使用软解。</summary>
    private DecodeResult? WithSoftwareFallback(Func<DecodeResult?> decode, CancellationToken token)
    {
        try
        {
            return decode();
        }
        catch (HardwareDecodeException exception)
        {
            token.ThrowIfCancellationRequested();
            Console.WriteLine($"Frame step hardware decoding failed: {exception.InnerException?.Message ?? exception.Message}");
            IsHardwareDecodingAvailable = false;
            if (!TryOpenSoftwareDecoder(useFullPacketDecoding))
                ThrowDecodeFailure();
            return decode();
        }
    }

    private DecodeResult? DecodeAdjacentFrameCore(long anchorPts, int direction, CancellationToken token)
    {
        if (direction > 0)
            return DecodeMatchingFrame(anchorPts, pts => pts > anchorPts, stopAfter: long.MaxValue, token);
        // 上一帧需要先知道其显示时间：解码到锚点为止，取锚点之前最晚的一帧，再精确解码该帧。
        long? previous = null;
        var distance = Math.Max(1, keyFrameGap);
        for (var attempt = 0; attempt < 8 && previous is null; attempt++, distance *= 2)
        {
            // 起点早于首帧时钳到首帧：否则解码出的画面都晚于起点，永远对不上起点，片头一个关键帧间隔内找不到上一帧。
            var start = Math.Max(firstFrameTimestamp, anchorPts - distance);
            DecodeMatchingFrame(start, pts =>
            {
                // 时间轴从首个关键帧开始，不退到片头关键帧之前显示的前导画面。
                if (pts < anchorPts && pts >= firstFrameTimestamp && (previous is null || pts > previous))
                    previous = pts;
                return false;
            }, stopAfter: anchorPts, token);
            if (previous is null && start <= firstFrameTimestamp)
                return null;
        }
        return previous is { } target
            ? DecodeMatchingFrame(target, pts => pts == target, stopAfter: target, token)
            : null;
    }

    /// <summary>
    /// 从不晚于 seekPts 的关键帧开始完整解码，返回第一帧满足条件的画面。
    /// 只有先看到不晚于 seekPts 的画面后才接受匹配，避免定位落在目标之后时跳帧；没看到这样的画面就从更早处重试；
    /// 解码输出超过 stopAfter 仍未匹配时结束。
    /// </summary>
    private DecodeResult? DecodeMatchingFrame(long seekPts, Func<long, bool> predicate, long stopAfter,
        CancellationToken token) =>
        WithFullDecoder(() => DecodeMatchingFrameCore(seekPts, predicate, stopAfter, token));

    private DecodeResult? DecodeMatchingFrameCore(long seekPts, Func<long, bool> predicate, long stopAfter,
        CancellationToken token)
    {
        var distance = Math.Max(1, keyFrameGap);
        for (var attempt = 0; attempt < 6; attempt++, distance *= 2)
        {
            var start = attempt == 0 ? seekPts : seekPts - distance;
            // 片头处 TS 带 B 帧时须从首帧之前预读，与关键帧导航相同。
            Seek(start <= firstFrameTimestamp ? firstFrameTimestamp - Math.Max(1, keyFrameGap * 2) : start,
                AVSEEK_FLAG.Backward);
            var anchored = false;
            var late = false;
            var passed = false;
            exactFrameFilter = pts =>
            {
                if (!anchored && pts > seekPts)
                    late = true;
                anchored |= pts <= seekPts;
                if (anchored && pts > stopAfter)
                    passed = true;
                return anchored && predicate(pts);
            };
            SetKeyFramesOnly(false);
            try
            {
                foreach (var packet in inFc.ReadPackets())
                {
                    token.ThrowIfCancellationRequested();
                    activeReadDeadline?.ThrowIfInterrupted();
                    if (packet.StreamIndex != videoStreamIndex)
                        continue;
                    var result = DecodePacket(packet, token, 0, 0);
                    if (result is not null)
                        return result;
                    if (late || passed)
                        break;
                }
                if (!late && !passed)
                {
                    var delayed = DecodePacket(null, token, 0, 0);
                    if (delayed is not null)
                        return delayed;
                }
            }
            finally
            {
                exactFrameFilter = null;
                // 丢弃逐帧解码遗留的重排序缓存，后续关键帧导航从当前文件位置继续读取下一个关键包。
                videoDecoder.FlushBuffers();
                SetKeyFramesOnly(true);
            }
            // 看到过不晚于 seekPts 的画面仍未匹配：确实没有目标。否则（先出现的画面已晚于 seekPts，或一帧都没有输出——
            // 按时间戳定位落在最后一个 GOP 中间，之后再无关键帧）从更早的位置重试。
            if (anchored || start <= firstFrameTimestamp)
                return null;
        }
        return null;
    }
}
