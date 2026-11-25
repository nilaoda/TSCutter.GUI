using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TSCutter.GUI.Models.Discs;
using TSCutter.GUI.Models.Media;
using TSCutter.GUI.Utils.Files;

namespace TSCutter.GUI.Services.Editing;

/// <summary>输出大小估算的可靠程度，决定预留多少余量。</summary>
internal enum ExportSizeAccuracy
{
    /// <summary>按源字节范围原样复制；只导出所选节目、删去轨道、去除空包时输出只会更小。</summary>
    Exact,
    /// <summary>首尾重新编码或重新封装，输出接近源字节范围。</summary>
    Approximate
}

/// <summary>导出中来自同一来源的一段：字节范围（不可用时起点为负）与时间范围（终点为 null 表示到来源结尾）。</summary>
internal readonly record struct ExportSizeSegment(long StartPosition, long EndPosition, double StartSeconds, double? EndSeconds);

/// <summary>估算一个输出文件所需空间的输入，在界面线程从导出任务取快照。</summary>
internal sealed record ExportSizeTask(
    string OutputPath,
    string SourcePath,
    IReadOnlyList<ExportSizeSegment> Segments,
    ExportSizeAccuracy Accuracy,
    bool M2tsOutput = false,
    DiscInput? Disc = null,
    AudioRebuildPlan? AudioRebuild = null,
    IReadOnlyList<string>? Sources = null);

/// <summary>一个输出文件的估算大小与为估算误差预留的余量。</summary>
internal readonly record struct ExportSpaceDemand(long Bytes, long MarginBytes)
{
    public long RequiredBytes => Bytes + MarginBytes;
}

/// <summary>一个卷上本次导出需要的空间（含余量与保留量）超过了可用空间。</summary>
internal sealed record ExportSpaceShortage(string VolumeRoot, long RequiredBytes, long AvailableBytes, int OutputCount);

/// <summary>
/// 导出前按卷估算顺序执行时的峰值占用，与卷上的可用空间比较。
/// 允许覆盖且在写入前删除的旧输出计入对应任务的空间释放量；
/// 后续任务尚未删除的旧文件不能抵扣当前任务的空间需求。
/// 估算不出或读取不到可用空间的输出不参与比较，不阻止导出。
/// </summary>
internal static class ExportSpaceCheck
{
    /// <summary>每个卷额外保留的空间，留给文件系统元数据与估算之外的小文件。</summary>
    public const long ReserveBytes = 256L << 20;

    /// <summary>读取可用空间（可能访问网络盘）的等待上限，超时按无法判断处理。</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private const double ApproximateMargin = 0.10;
    // 拿不到字节位置时按时长占比估算，码率随画面变化，留更大的余量；转码音轨同样按此计。
    private const double ProportionalMargin = 0.25;
    // DVD-Video 节目流的最大复用码率为 10.08 Mbps。
    private const double DvdMaxBytesPerSecond = 10_080_000 / 8.0;
    // 192 字节的 M2TS 包比 188 字节的 TS 包多 4 字节时间码。
    private const double M2tsPacketRatio = 192.0 / 188;
    // 音频写入 TS 时的包头与 PES 头开销。
    private const double TsAudioOverhead = 1.07;
    // SMPTE 302M 每声道每个采样按 24 位样本加附加位计算，约 3.5 字节；FLAC 不会超过同样声道数的 PCM。
    private const double PcmBytesPerSample = 3.5;
    private const int SampleRate = 48_000;
    // 杜比 E 最多 8 声道，多轨合并的结果为 5.1。
    private const int DolbyEMaxChannels = 8;
    private const int ChannelMergeChannels = 6;

    /// <summary>
    /// 估算一个输出文件的大小：有字节范围时按范围计算，否则按时长占来源总时长的比例计算；
    /// 光盘标题按标题的字节数（蓝光）或 DVD 的最大码率计算。估算不出时返回 null。
    /// </summary>
    public static ExportSpaceDemand? Estimate(ExportSizeTask task, Func<string, double?>? sourceDuration = null)
    {
        if (task.Segments.Count == 0)
            return null;
        long? fileLength = null;
        long FileLength() => fileLength ??= new FileInfo(task.SourcePath).Length;
        double bytes = 0, margin = 0, seconds = 0;
        foreach (var segment in task.Segments)
        {
            var end = segment.EndSeconds ?? SourceDuration(task, sourceDuration);
            double? segmentSeconds = end is { } value ? Math.Max(0, value - segment.StartSeconds) : null;
            double segmentBytes;
            double rate;
            // 片段起点位置未知时按 0 记录：重新封装与智能剪辑的“0 到文件结尾”不一定代表整个文件，能按时长估算时以时长为准。
            var wholeFileRange = task.Accuracy != ExportSizeAccuracy.Exact && segment.StartPosition <= 0 && segment.EndPosition <= 0;
            if (wholeFileRange && segmentSeconds is { } span && ProportionalBytes(task, span, sourceDuration, FileLength) is { } share)
            {
                (segmentBytes, rate) = share;
            }
            else if (task.Disc is null && segment.StartPosition >= 0 &&
                (segment.EndPosition > 0 ? segment.EndPosition : FileLength()) is var rangeEnd && rangeEnd > segment.StartPosition)
            {
                segmentBytes = rangeEnd - segment.StartPosition;
                rate = task.Accuracy == ExportSizeAccuracy.Exact ? 0 : ApproximateMargin;
            }
            else if (segmentSeconds is { } duration && ProportionalBytes(task, duration, sourceDuration, FileLength) is { } proportional)
            {
                (segmentBytes, rate) = proportional;
            }
            else
                return null;
            if (task.M2tsOutput)
                segmentBytes *= M2tsPacketRatio;
            bytes += segmentBytes;
            margin += segmentBytes * rate;
            seconds += segmentSeconds ?? 0;
        }
        if (task.AudioRebuild is { HasWork: true } plan && seconds > 0)
        {
            var audio = AudioBytesPerSecond(plan) * seconds;
            bytes += audio;
            margin += audio * ProportionalMargin;
        }
        return new ExportSpaceDemand((long)Math.Ceiling(bytes), (long)Math.Ceiling(margin));
    }

    private static double? SourceDuration(ExportSizeTask task, Func<string, double?>? sourceDuration) =>
        task.Disc is { } disc ? disc.Duration.TotalSeconds : sourceDuration?.Invoke(task.SourcePath);

    private static (double Bytes, double Margin)? ProportionalBytes(ExportSizeTask task, double seconds,
        Func<string, double?>? sourceDuration, Func<long> fileLength)
    {
        if (task.Disc is { Kind: DiscKind.Dvd })
            return (seconds * DvdMaxBytesPerSecond, ApproximateMargin);
        double totalBytes;
        if (task.Disc is { } disc)
        {
            if (disc.Segments.Count == 0 || disc.Segments.Any(item => item.TitleStartByte < 0 || item.TitleEndByte <= item.TitleStartByte))
                return null;
            totalBytes = disc.Segments.Sum(item => (double)(item.TitleEndByte - item.TitleStartByte));
        }
        else
            totalBytes = fileLength();
        if (SourceDuration(task, sourceDuration) is not { } total || !(total > 0))
            return null;
        return (totalBytes * Math.Min(1, seconds / total), ProportionalMargin);
    }

    /// <summary>重建音轨新增输出的码率上限（字节/秒）：有损编码按设定码率，PCM 与 FLAC 按未压缩的采样计算。</summary>
    private static double AudioBytesPerSecond(AudioRebuildPlan plan)
    {
        var total = 0.0;
        foreach (var track in plan.Tracks.Where(track => track.HasWork))
            total += track.Outputs.Where(output => output.Enabled).Sum(output => OutputBytesPerSecond(output, DolbyEMaxChannels));
        foreach (var merge in plan.Merges.Where(merge => merge.HasWork))
            total += merge.Outputs.Where(output => output.Enabled).Sum(output => OutputBytesPerSecond(output, ChannelMergeChannels));
        return total;
    }

    private static double OutputBytesPerSecond(AudioOutput output, int sourceChannels)
    {
        var raw = AudioOutputCodecInfo.IsLossless(output.Codec)
            ? AudioOutputCodecInfo.OutputChannels(output.Codec, output.Layout, sourceChannels) * SampleRate * PcmBytesPerSample
            : output.BitrateKbps * 1000 / 8.0;
        return raw * TsAudioOverhead;
    }

    /// <summary>
    /// 按执行顺序计算各卷累计占用的最大值，返回空间不足的卷。每个卷另加 <see cref="ReserveBytes"/>。
    /// 读取可用空间与来源文件长度都会访问磁盘，应在后台线程调用。
    /// </summary>
    public static IReadOnlyList<ExportSpaceShortage> FindShortages(IEnumerable<ExportSizeTask> tasks,
        Func<string, double?>? sourceDuration, Func<string, DiskSpaceInfo?> probe,
        Func<string, string?>? existingFileVolume = null)
    {
        existingFileVolume ??= DiskSpaceUtil.TryGetVolumeId;
        var orderedTasks = tasks.ToArray();
        var sources = orderedTasks.SelectMany(task => task.Sources ?? [task.SourcePath])
            .Concat(orderedTasks.Where(task => task.Disc is not null).Select(task => task.Disc!.SourcePath)).ToArray();
        var reclaimed = new HashSet<string>(MediaFileUtil.PathComparer);
        var spaceByDirectory = new Dictionary<string, DiskSpaceInfo?>(MediaFileUtil.PathComparer);
        var volumes = new Dictionary<string, (string Root, long Required, long Peak, long Available, int Count)>(MediaFileUtil.PathComparer);
        var order = new List<string>();
        foreach (var task in orderedTasks)
        {
            ExportSpaceDemand? demand;
            string directory;
            try
            {
                demand = Estimate(task, sourceDuration);
                directory = Path.GetDirectoryName(Path.GetFullPath(task.OutputPath)) ?? task.OutputPath;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
                                                  or NotSupportedException)
            {
                // 来源暂时不可读等情况只跳过这一项的估算。
                continue;
            }
            if (demand is null)
                continue;
            if (!spaceByDirectory.TryGetValue(directory, out var space))
                spaceByDirectory[directory] = space = probe(task.OutputPath);
            if (space is not { } info)
                continue;
            if (!volumes.TryGetValue(info.VolumeId, out var volume))
            {
                order.Add(info.VolumeId);
                volume = (info.VolumeRoot, 0, long.MinValue, info.AvailableBytes, 0);
            }
            long released = 0;
            try
            {
                var path = MediaFileUtil.CanonicalPath(task.OutputPath);
                if (File.Exists(task.OutputPath) &&
                    !File.GetAttributes(task.OutputPath).HasFlag(FileAttributes.ReparsePoint) &&
                    MediaFileAccess.Current.CanOverwrite(task.OutputPath) &&
                    sources.All(source => !MediaFileUtil.ConflictsWithSource(task.OutputPath, source)) &&
                    MediaFileUtil.PathComparer.Equals(existingFileVolume(task.OutputPath), info.VolumeId) &&
                    reclaimed.Add(path))
                    released = new FileInfo(task.OutputPath).Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // 无法确认将释放的文件和卷时，只计算新输出所需空间。
            }
            var required = volume.Required + demand.Value.RequiredBytes - released;
            volumes[info.VolumeId] = volume with
            {
                Required = required, Peak = Math.Max(volume.Peak, required),
                Available = Math.Min(volume.Available, info.AvailableBytes), Count = volume.Count + 1
            };
        }
        return order.Select(id => volumes[id])
            .Where(volume => volume.Peak + ReserveBytes > volume.Available)
            .Select(volume => new ExportSpaceShortage(volume.Root, volume.Peak + ReserveBytes, volume.Available,
                volume.Count))
            .ToArray();
    }

    /// <summary>
    /// 在后台检查空间，最多等待 <see cref="Timeout"/>；超时或任何失败都返回空列表，不阻止导出。
    /// </summary>
    public static async Task<IReadOnlyList<ExportSpaceShortage>> FindShortagesAsync(IReadOnlyList<ExportSizeTask> tasks,
        Func<string, double?>? sourceDuration, Func<string, DiskSpaceInfo?>? probe = null, TimeSpan? timeout = null)
    {
        if (tasks.Count == 0)
            return [];
        try
        {
            return await Task.Run(() => FindShortages(tasks, sourceDuration, probe ?? DiskSpaceUtil.TryGetFreeSpace))
                .WaitAsync(timeout ?? Timeout).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Free space check skipped: {exception.Message}");
            return [];
        }
    }
}
