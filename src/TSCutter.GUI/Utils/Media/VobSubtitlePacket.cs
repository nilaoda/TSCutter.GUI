using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace TSCutter.GUI.Utils.Media;

/// <summary>裁剪 DVD SPU 的显示控制时间，保留像素、调色板和透明度命令。</summary>
internal static class VobSubtitlePacket
{
    internal sealed record Clipped(byte[] Bytes, double Start, double Duration);
    private readonly record struct Control(int Offset, ushort Date);
    private const double TickSeconds = 1024.0 / 90_000;

    public static Clipped? Clip(ReadOnlySpan<byte> packet, double packetTime, double start, double end)
    {
        if (packet.Length < 10 || BinaryPrimitives.ReadUInt16BigEndian(packet) != packet.Length)
            throw new InvalidDataException("Invalid DVD subtitle packet.");
        var controls = new List<Control>();
        var offset = (int)BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        double shown = double.PositiveInfinity, cleared = double.PositiveInfinity;
        while (true)
        {
            if (offset < 4 || offset > packet.Length - 5)
                throw new InvalidDataException("Invalid DVD subtitle control offset.");
            var date = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            controls.Add(new Control(offset, date));
            var next = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]);
            var position = offset + 4;
            var terminated = false;
            while (position < packet.Length)
            {
                var command = packet[position++];
                if (command == 0xFF)
                {
                    terminated = true;
                    break;
                }
                if (command is 0 or 1)
                    shown = Math.Min(shown, packetTime + date * TickSeconds);
                if (command == 2)
                    cleared = Math.Min(cleared, packetTime + date * TickSeconds);
                var length = command switch
                {
                    0 or 1 or 2 => 0,
                    3 or 4 => 2,
                    5 => 6,
                    6 => 4,
                    7 when position <= packet.Length - 2 => BinaryPrimitives.ReadUInt16BigEndian(packet[position..]),
                    _ => throw new InvalidDataException("Unsupported DVD subtitle control command.")
                };
                if (length > packet.Length - position)
                    throw new InvalidDataException("Truncated DVD subtitle control command.");
                position += length;
            }
            if (!terminated)
                throw new InvalidDataException("Truncated DVD subtitle controls.");
            if (next == offset)
                break;
            if (next <= offset)
                throw new InvalidDataException("Cyclic DVD subtitle controls.");
            offset = next;
        }
        if (shown >= end || cleared <= start || !double.IsFinite(shown))
            return null;
        var baseTime = Math.Max(packetTime, start);
        var finish = Math.Min(cleared, end);
        var appendStop = !double.IsFinite(cleared);
        if (finish <= baseTime)
            return null;
        var bytes = new byte[packet.Length + (appendStop ? 6 : 0)];
        if (bytes.Length > ushort.MaxValue)
            throw new InvalidDataException("DVD subtitle packet is too large.");
        packet.CopyTo(bytes);
        foreach (var control in controls)
        {
            var date = Date(Math.Clamp(packetTime + control.Date * TickSeconds, baseTime, finish) - baseTime);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(control.Offset), date);
        }
        if (appendStop)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)bytes.Length);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(controls[^1].Offset + 2), (ushort)packet.Length);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(packet.Length), Date(finish - baseTime));
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(packet.Length + 2), (ushort)packet.Length);
            bytes[^2] = 2;
            bytes[^1] = 0xFF;
        }
        return new Clipped(bytes, baseTime, finish - baseTime);
    }

    private static ushort Date(double seconds)
    {
        var date = Math.Round(seconds / TickSeconds);
        if (date < 0 || date > ushort.MaxValue)
            throw new InvalidDataException("DVD subtitle display time is out of range.");
        return (ushort)date;
    }
}
