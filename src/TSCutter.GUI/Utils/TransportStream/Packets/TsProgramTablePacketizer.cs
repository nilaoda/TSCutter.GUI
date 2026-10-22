using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace TSCutter.GUI.Utils.TransportStream.Packets;

internal static class TsProgramTablePacketizer
{
    internal static byte[] Create(IReadOnlyList<(int Pid, byte[] Data)> tables,
        TsPacketLayout layout, uint arrivalHeader, int[] lastPayloadContinuity)
    {
        var packetCount = tables.Sum(table => (table.Data.Length + 184) / 184);
        var result = new byte[checked(packetCount * layout.PacketSize)];
        var position = 0;
        foreach (var (pid, data) in tables)
        {
            var offset = 0;
            while (offset < data.Length)
            {
                if (layout.IsM2ts)
                    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(position), arrivalHeader);
                var packet = layout.GetPacketBody(result.AsSpan(), position);
                packet.Fill(0xff);
                var continuity = (lastPayloadContinuity[pid] + 1) & 15;
                packet[0] = 0x47;
                packet[1] = (byte)(pid >> 8 | (offset == 0 ? 0x40 : 0));
                packet[2] = (byte)pid;
                packet[3] = (byte)(0x10 | continuity);
                lastPayloadContinuity[pid] = continuity;
                var payload = 4;
                if (offset == 0) packet[payload++] = 0;
                var count = Math.Min(data.Length - offset, 188 - payload);
                data.AsSpan(offset, count).CopyTo(packet[payload..]);
                offset += count;
                position += layout.PacketSize;
            }
        }
        return result;
    }
}
