using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using TSCutter.GUI.Models;

namespace TSCutter.GUI.Utils;

internal static class TsPsiSectionBuilder
{
    internal readonly record struct PatProgram(int ProgramNumber, int PmtPid);
    internal readonly record struct PmtStream(int Pid, TsStreamDefinition Definition);
    internal readonly record struct SdtService(
        int ServiceId,
        byte[] Descriptors,
        bool EitSchedule,
        bool EitPresentFollowing,
        byte RunningStatus,
        bool FreeCaMode);

    public static byte[] BuildPat(int transportStreamId, byte version, IReadOnlyList<PatProgram> programs)
    {
        var sectionLength = 9 + programs.Count * 4;
        if (sectionLength > 1021)
            throw new TsFilterException(TsFilterErrorCode.PatTooLarge);

        var section = new byte[3 + sectionLength];
        section[0] = 0x00;
        section[1] = (byte)(0xB0 | (sectionLength >> 8));
        section[2] = (byte)sectionLength;
        section[3] = (byte)(transportStreamId >> 8);
        section[4] = (byte)transportStreamId;
        section[5] = (byte)(0xC1 | ((version & 0x1F) << 1));
        section[6] = 0;
        section[7] = 0;
        var offset = 8;
        foreach (var program in programs)
        {
            section[offset++] = (byte)(program.ProgramNumber >> 8);
            section[offset++] = (byte)program.ProgramNumber;
            section[offset++] = (byte)(0xE0 | (program.PmtPid >> 8));
            section[offset++] = (byte)program.PmtPid;
        }
        WriteCrc(section);
        return section;
    }

    public static byte[] BuildPmt(
        int programNumber,
        byte version,
        int pcrPid,
        ReadOnlySpan<byte> programDescriptors,
        IReadOnlyList<PmtStream> streams)
    {
        var sectionLength = 13 + programDescriptors.Length +
                            streams.Sum(item => 5 + item.Definition.Descriptors.Length);
        if (sectionLength > 1021)
            throw new TsFilterException(TsFilterErrorCode.PmtTooLarge, programNumber);

        var section = new byte[3 + sectionLength];
        section[0] = 0x02;
        section[1] = (byte)(0xB0 | (sectionLength >> 8));
        section[2] = (byte)sectionLength;
        section[3] = (byte)(programNumber >> 8);
        section[4] = (byte)programNumber;
        section[5] = (byte)(0xC1 | ((version & 0x1F) << 1));
        section[6] = 0;
        section[7] = 0;
        section[8] = (byte)(0xE0 | (pcrPid >> 8));
        section[9] = (byte)pcrPid;
        section[10] = (byte)(0xF0 | (programDescriptors.Length >> 8));
        section[11] = (byte)programDescriptors.Length;
        var offset = 12;
        programDescriptors.CopyTo(section.AsSpan(offset));
        offset += programDescriptors.Length;
        foreach (var stream in streams)
        {
            section[offset++] = stream.Definition.StreamType;
            section[offset++] = (byte)(0xE0 | (stream.Pid >> 8));
            section[offset++] = (byte)stream.Pid;
            section[offset++] = (byte)(0xF0 | (stream.Definition.Descriptors.Length >> 8));
            section[offset++] = (byte)stream.Definition.Descriptors.Length;
            stream.Definition.Descriptors.CopyTo(section, offset);
            offset += stream.Definition.Descriptors.Length;
        }
        WriteCrc(section);
        return section;
    }

    public static byte[] BuildSdt(
        int transportStreamId,
        byte version,
        int originalNetworkId,
        IReadOnlyList<SdtService> services)
    {
        var sectionLength = 12 + services.Sum(item => 5 + item.Descriptors.Length);
        if (sectionLength > 1021)
            throw new TsFilterException(TsFilterErrorCode.SdtTooLarge);

        var section = new byte[3 + sectionLength];
        section[0] = 0x42;
        section[1] = (byte)(0xF0 | (sectionLength >> 8));
        section[2] = (byte)sectionLength;
        section[3] = (byte)(transportStreamId >> 8);
        section[4] = (byte)transportStreamId;
        section[5] = (byte)(0xC1 | ((version & 0x1F) << 1));
        section[6] = 0;
        section[7] = 0;
        section[8] = (byte)(originalNetworkId >> 8);
        section[9] = (byte)originalNetworkId;
        section[10] = 0xFF;
        var offset = 11;
        foreach (var service in services)
        {
            section[offset++] = (byte)(service.ServiceId >> 8);
            section[offset++] = (byte)service.ServiceId;
            section[offset++] = (byte)(0xFC |
                                       (service.EitSchedule ? 0x02 : 0) |
                                       (service.EitPresentFollowing ? 0x01 : 0));
            section[offset++] = (byte)(((service.RunningStatus & 0x07) << 5) |
                                       (service.FreeCaMode ? 0x10 : 0) |
                                       (service.Descriptors.Length >> 8));
            section[offset++] = (byte)service.Descriptors.Length;
            service.Descriptors.CopyTo(section, offset);
            offset += service.Descriptors.Length;
        }
        WriteCrc(section);
        return section;
    }

    public static void WriteCrc(Span<byte> section)
    {
        var crc = ComputeCrc(section[..^4]);
        BinaryPrimitives.WriteUInt32BigEndian(section[^4..], crc);
    }

    public static bool HasValidCrc(ReadOnlySpan<byte> section) => ComputeCrc(section) == 0;

    private static uint ComputeCrc(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= (uint)value << 24;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }
        return crc;
    }
}
