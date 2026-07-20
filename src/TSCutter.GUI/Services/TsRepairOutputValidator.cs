using System;
using System.Collections.Generic;
using TSCutter.GUI.Models;

namespace TSCutter.GUI.Services;

/// <summary>
/// 在修复结果写盘时同步检查所选 PID，不保存事件、时间轴或媒体负载。
/// </summary>
internal sealed class TsRepairOutputValidator(IReadOnlySet<int> selectedPids)
{
    private const int PacketSize = TsStreamAnalyzer.PacketSize;
    private readonly Dictionary<int, PidState> _states = [];

    public long ContinuityErrors { get; private set; }
    public long TransportErrors { get; private set; }
    public long PesSizeErrors { get; private set; }
    public long TotalErrors => ContinuityErrors + TransportErrors + PesSizeErrors;

    public void ProcessPackets(ReadOnlySpan<byte> packets)
    {
        if (packets.Length % PacketSize != 0)
            throw new TsRepairException(TsRepairErrorCode.InvalidRepairData);

        for (var offset = 0; offset < packets.Length; offset += PacketSize)
            ProcessPacket(packets.Slice(offset, PacketSize));
    }

    private void ProcessPacket(ReadOnlySpan<byte> packet)
    {
        if (packet[0] != 0x47)
            throw new TsRepairException(TsRepairErrorCode.InvalidRepairData);

        var pid = ((packet[1] & 0x1F) << 8) | packet[2];
        if (!selectedPids.Contains(pid))
            return;

        if (!_states.TryGetValue(pid, out var state))
        {
            state = new PidState();
            _states[pid] = state;
        }

        var transportError = (packet[1] & 0x80) != 0;
        var payloadStart = (packet[1] & 0x40) != 0;
        var adaptationControl = (packet[3] >> 4) & 0x03;
        var continuityCounter = packet[3] & 0x0F;
        var hasAdaptation = (adaptationControl & 0x02) != 0;
        var hasPayload = (adaptationControl & 0x01) != 0;

        if (transportError)
        {
            TransportErrors++;
            state.DiscardPes();
        }
        if (adaptationControl == 0)
        {
            state.DiscardPes();
            return;
        }

        var payloadOffset = 4;
        var discontinuity = false;
        if (hasAdaptation)
        {
            var adaptationLength = packet[4];
            if (adaptationLength > 183)
            {
                state.DiscardPes();
                return;
            }
            payloadOffset += adaptationLength + 1;
            if (adaptationLength > 0)
                discontinuity = (packet[5] & 0x80) != 0;
        }

        if (discontinuity)
        {
            state.HasContinuity = false;
            state.DiscardPes();
        }

        if (!hasPayload || payloadOffset >= PacketSize)
            return;

        if (!ProcessContinuity(packet, state, continuityCounter))
            return;

        // TEI 包的负载不可信。仍跟踪它的 CC，但不能参与 PES 长度结算。
        if (transportError)
            return;

        ProcessPesSize(packet[payloadOffset..], payloadStart, state);
    }

    private bool ProcessContinuity(ReadOnlySpan<byte> packet, PidState state, int counter)
    {
        var packetBody = packet[4..];
        if (state.HasContinuity)
        {
            var expected = (state.LastContinuityCounter + 1) & 0x0F;
            if (counter == state.LastContinuityCounter)
            {
                // 与快速检查保持一致：重复包本身不计入 CC 缺口；内容冲突时只放弃当前 PES。
                if (!packetBody.SequenceEqual(state.LastPacketBody))
                    state.DiscardPes();
                return false;
            }
            if (counter != expected)
            {
                ContinuityErrors++;
                state.DiscardPes();
            }
        }

        state.HasContinuity = true;
        state.LastContinuityCounter = counter;
        packetBody.CopyTo(state.LastPacketBody);
        return true;
    }

    private void ProcessPesSize(ReadOnlySpan<byte> payload, bool payloadStart, PidState state)
    {
        if (payloadStart)
        {
            FinishPes(state);
            state.PesActive = true;
            state.PesExpectedLength = -1;
            state.PesActualLength = 0;
            state.PesPrefix = 0;
            state.PesPrefixLength = 0;
        }
        else if (!state.PesActive)
        {
            return;
        }

        state.PesActualLength += payload.Length;
        if (state.PesExpectedLength >= 0)
            return;

        // PES 起始的 6 字节可能跨 TS 包，使用定长整数拼接，不为每个 PES 分配缓冲区。
        var needed = 6 - state.PesPrefixLength;
        var copyLength = Math.Min(needed, payload.Length);
        for (var index = 0; index < copyLength; index++)
            state.PesPrefix = (state.PesPrefix << 8) | payload[index];
        state.PesPrefixLength += copyLength;
        if (state.PesPrefixLength < 6)
            return;

        if ((state.PesPrefix >> 24) != 0x000001)
        {
            state.DiscardPes();
            return;
        }

        var declaredLength = (int)(state.PesPrefix & 0xFFFF);
        state.PesExpectedLength = declaredLength == 0 ? 0 : declaredLength + 6;
    }

    private void FinishPes(PidState state)
    {
        if (!state.PesActive)
            return;

        var expectedLength = state.PesExpectedLength;
        var actualLength = state.PesActualLength;
        state.DiscardPes();
        // 文件尾的最后一个未闭合 PES 与快速检查一样不结算，只有遇到下一 PUSI 才能确认边界。
        if (expectedLength > 0 && actualLength != expectedLength)
            PesSizeErrors++;
    }

    private sealed class PidState
    {
        public bool HasContinuity;
        public int LastContinuityCounter;
        public byte[] LastPacketBody { get; } = new byte[PacketSize - 4];
        public bool PesActive;
        public int PesExpectedLength = -1;
        public long PesActualLength;
        public ulong PesPrefix;
        public int PesPrefixLength;

        public void DiscardPes()
        {
            PesActive = false;
            PesExpectedLength = -1;
            PesActualLength = 0;
            PesPrefix = 0;
            PesPrefixLength = 0;
        }
    }
}
