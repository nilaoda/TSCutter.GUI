using System;
using System.Collections.Generic;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Utils.TransportStream.Packets;

namespace TSCutter.GUI.Utils.Editing;

// 视频切点可能落在音频 PES 中间。丢弃片头无头续包，仅在片尾补齐已开始且长度明确的 PES。
// 不缓存音频载荷，也不修复片段内部原有的丢包、TEI 或错误长度。
internal sealed class TsClipMergeAudioBoundary
{
    private readonly AudioState?[] lookup = new AudioState?[8192];
    private readonly List<AudioState> states = [];

    public TsClipMergeAudioBoundary(IReadOnlyList<int> audioPids, int videoPid)
    {
        foreach (var pid in audioPids)
        {
            if (pid is < 0 or >= 8192 || pid == videoPid || lookup[pid] is not null)
                continue;
            var state = new AudioState();
            lookup[pid] = state;
            states.Add(state);
        }
    }

    public bool HasPending
    {
        get
        {
            foreach (var state in states)
                if (state.HasPending)
                    return true;
            return false;
        }
    }

    public void BeginSegment()
    {
        foreach (var state in states)
            state.Reset();
    }

    public bool KeepPacket(ReadOnlySpan<byte> packet, bool beyondEnd)
    {
        var pid = ((packet[1] & 31) << 8) | packet[2];
        if (lookup[pid] is not { } state)
            return !beyondEnd;
        if (!TsPacketParser.TryParse(packet, out var info) || info.TransportError || info.ScramblingControl != 0)
        {
            state.Reliable = false;
            return !beyondEnd;
        }
        if (!info.HasPayload)
            return !beyondEnd || state.HasPending;
        // 重复的首包也可能落在切点后，必须先识别重复，不能误认为遇到了下一 PES。
        if (state.Started && info.ContinuityCounter == state.LastContinuity && packet.SequenceEqual(state.Previous))
            return true;
        // 越过视频终点后不引入新的音频 PES；遇到新头仍未补齐，说明源流本身已经残缺。
        if (beyondEnd && (info.PayloadStart || !state.HasPending))
        {
            state.Reliable = false;
            return false;
        }
        if (info.PayloadStart)
        {
            state.Reset();
            state.Started = true;
        }
        else if (!state.Started)
        {
            return false;
        }
        if (state.LastContinuity >= 0)
        {
            if (info.Discontinuity || info.ContinuityCounter != ((state.LastContinuity + 1) & 15))
                state.Reliable = false;
        }
        state.LastContinuity = info.ContinuityCounter;
        packet.CopyTo(state.Previous);
        var payload = packet[info.PayloadOffset..];
        state.ActualLength += payload.Length;
        for (var index = 0; state.PrefixLength < 6 && index < payload.Length; index++)
        {
            state.Prefix = (state.Prefix << 8) | payload[index];
            state.PrefixLength++;
        }
        if (state.PrefixLength == 6)
        {
            if ((state.Prefix >> 24) != 0x000001)
                state.Reliable = false;
            var declared = (int)(state.Prefix & 0xFFFF);
            state.ExpectedLength = declared == 0 ? 0 : declared + 6;
        }
        return true;
    }

    private sealed class AudioState
    {
        public bool Started;
        public bool Reliable;
        public int PrefixLength;
        public ulong Prefix;
        public long ActualLength;
        public int ExpectedLength;
        public int LastContinuity;
        public readonly byte[] Previous = new byte[TsUtil.TsPacketSize];
        public bool HasPending => Started && Reliable && (PrefixLength < 6 || ExpectedLength > ActualLength);

        public void Reset()
        {
            Started = false;
            Reliable = true;
            PrefixLength = 0;
            Prefix = 0;
            ActualLength = 0;
            ExpectedLength = 0;
            LastContinuity = -1;
        }
    }
}
