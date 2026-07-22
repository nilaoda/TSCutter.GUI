using System;
using System.Collections.Generic;
using TSCutter.GUI.Models;

namespace TSCutter.GUI.Services;

internal readonly record struct TsElementaryRepairLayoutSegment(
    int ElementaryOffset,
    int ElementaryLength,
    byte[]? PesHeader,
    int PacketCount)
{
    public int PayloadLength => ElementaryLength + (PesHeader?.Length ?? 0);
}

internal static class TsElementaryRepairPacketizer
{
    private const int MaximumPayloadLength = 184;

    public static bool TryCreateLayout(
        int elementaryLength,
        IReadOnlyList<TsRepairPesBoundary> pesBoundaries,
        int packetCount,
        out TsElementaryRepairLayoutSegment[] layout)
    {
        layout = [];
        if (elementaryLength <= 0 || packetCount <= 0)
            return false;

        var segments = new List<(int Offset, int Length, byte[]? Header)>();
        var previousOffset = 0;
        for (var index = 0; index < pesBoundaries.Count; index++)
        {
            var boundary = pesBoundaries[index];
            if (boundary.ElementaryOffset < previousOffset ||
                boundary.ElementaryOffset >= elementaryLength ||
                !IsCompletePesHeader(boundary.PesHeader))
            {
                return false;
            }
            if (index > 0 && boundary.ElementaryOffset == previousOffset)
                return false;

            if (boundary.ElementaryOffset > previousOffset)
            {
                var previousHeader = index == 0 ? null : pesBoundaries[index - 1].PesHeader;
                segments.Add((previousOffset, boundary.ElementaryOffset - previousOffset, previousHeader));
            }
            else if (index > 0)
            {
                return false;
            }
            previousOffset = boundary.ElementaryOffset;
        }

        var finalHeader = pesBoundaries.Count == 0 ? null : pesBoundaries[^1].PesHeader;
        segments.Add((previousOffset, elementaryLength - previousOffset, finalHeader));

        var minimumCounts = new int[segments.Count];
        var maximumCounts = new int[segments.Count];
        var totalMinimum = 0;
        var totalMaximum = 0;
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var payloadLength = segment.Length + (segment.Header?.Length ?? 0);
            if (payloadLength <= 0)
                return false;
            minimumCounts[index] = (payloadLength + MaximumPayloadLength - 1) / MaximumPayloadLength;
            // 每个合成包都必须至少携带一个负载字节，CC 才能逐包递增；PUSI 包还必须
            // 在第一个包内完整容纳 PES 头，因此它可拆分出的最大包数会相应减少。
            maximumCounts[index] = segment.Header is null
                ? payloadLength
                : payloadLength - segment.Header.Length + 1;
            if (maximumCounts[index] < minimumCounts[index])
                return false;
            totalMinimum += minimumCounts[index];
            totalMaximum += maximumCounts[index];
        }
        if (packetCount < totalMinimum || packetCount > totalMaximum)
            return false;

        var counts = minimumCounts;
        var extraPackets = packetCount - totalMinimum;
        while (extraPackets-- > 0)
        {
            var selected = -1;
            var highestLoad = double.MinValue;
            for (var index = 0; index < segments.Count; index++)
            {
                if (counts[index] >= maximumCounts[index])
                    continue;
                var payloadLength = segments[index].Length + (segments[index].Header?.Length ?? 0);
                var load = payloadLength / (double)counts[index];
                if (load <= highestLoad)
                    continue;
                highestLoad = load;
                selected = index;
            }
            if (selected < 0)
                return false;
            counts[selected]++;
        }

        layout = new TsElementaryRepairLayoutSegment[segments.Count];
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            layout[index] = new TsElementaryRepairLayoutSegment(
                segment.Offset, segment.Length, segment.Header, counts[index]);
        }
        return true;
    }

    public static int GetNextPayloadLength(
        TsElementaryRepairLayoutSegment segment,
        int payloadOffset,
        int packetIndex)
    {
        var packetsRemaining = segment.PacketCount - packetIndex;
        var bytesRemaining = segment.PayloadLength - payloadOffset;
        if (packetsRemaining <= 0 || bytesRemaining < packetsRemaining)
            return 0;

        var minimumCurrent = packetIndex == 0 && segment.PesHeader is { } header
            ? header.Length
            : 1;
        var maximumCurrent = Math.Min(MaximumPayloadLength, bytesRemaining - packetsRemaining + 1);
        var balanced = (bytesRemaining + packetsRemaining - 1) / packetsRemaining;
        return Math.Clamp(balanced, minimumCurrent, maximumCurrent);
    }

    public static void CopyPayload(
        ReadOnlySpan<byte> elementaryPayload,
        TsElementaryRepairLayoutSegment segment,
        int payloadOffset,
        Span<byte> destination)
    {
        var copied = 0;
        var header = segment.PesHeader;
        if (header is not null && payloadOffset < header.Length)
        {
            var headerLength = Math.Min(destination.Length, header.Length - payloadOffset);
            header.AsSpan(payloadOffset, headerLength).CopyTo(destination);
            copied += headerLength;
            payloadOffset += headerLength;
        }

        if (copied == destination.Length)
            return;
        var headerLengthTotal = header?.Length ?? 0;
        var elementaryOffset = segment.ElementaryOffset + Math.Max(0, payloadOffset - headerLengthTotal);
        elementaryPayload.Slice(elementaryOffset, destination.Length - copied)
            .CopyTo(destination[copied..]);
    }

    private static bool IsCompletePesHeader(byte[] header) =>
        header.Length is >= 9 and <= MaximumPayloadLength &&
        header[0] == 0 && header[1] == 0 && header[2] == 1 &&
        9 + header[8] == header.Length;
}
