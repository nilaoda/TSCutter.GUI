using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Utils.Media;

namespace TSCutter.GUI.Services.Editing;

// PGS 的普通显示集合可以复用较早的调色板、窗口和 RLE 对象。
// 在剪辑起点重建完整 epoch，保留压缩图形数据，不解码或重新编码字幕。
internal sealed class PgsSubtitleState
{
    private const int MaximumStateBytes = 64 * 1024 * 1024;
    private readonly Dictionary<byte, Palette> palettes = [];
    private readonly Dictionary<ushort, GraphicObject> objects = [];
    private readonly Dictionary<byte, byte[]> windows = [];
    private byte[]? presentation;
    private int objectBytes;
    private bool hasEpoch;
    private bool pendingDisplaySet;

    private sealed class Palette(byte version)
    {
        public byte Version = version;
        public SortedDictionary<byte, byte[]> Entries { get; } = [];
    }

    private sealed class GraphicObject(byte version, int expected)
    {
        public byte Version { get; } = version;
        public int Expected { get; } = expected;
        public int Received;
        public bool Complete;
        public List<byte[]> Segments { get; } = [];
    }

    public void Read(ReadOnlySpan<byte> data)
    {
        try
        {
            if (PgsSubtitlePacket.Decompress(data) is { } decompressed)
                data = decompressed;
        }
        catch (InvalidDataException) { throw Invalid(); }
        while (!data.IsEmpty)
        {
            if (data.Length < 3)
                throw Invalid();
            var kind = data[0];
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[1..]);
            if (length > data.Length - 3)
                throw Invalid();
            var payload = data.Slice(3, length);
            switch (kind)
            {
                case 0x16:
                    if (payload.Length < 11)
                        throw Invalid();
                    if ((payload[7] & 0x80) != 0)
                    {
                        hasEpoch = true;
                        palettes.Clear();
                        objects.Clear();
                        windows.Clear();
                        objectBytes = 0;
                    }
                    presentation = payload.ToArray();
                    pendingDisplaySet = true;
                    break;
                case 0x80:
                    if (!payload.IsEmpty)
                        throw Invalid();
                    pendingDisplaySet = false;
                    break;
                case 0x14:
                    ReadPalette(payload);
                    break;
                case 0x15:
                    ReadObject(payload);
                    break;
                case 0x17:
                    if (payload.IsEmpty || payload.Length != 1 + payload[0] * 9)
                        throw Invalid();
                    for (var i = 1; i < payload.Length; i += 9)
                        windows[payload[i]] = payload.Slice(i, 9).ToArray();
                    break;
            }
            data = data[(3 + length)..];
        }
    }

    private void ReadPalette(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2 || (payload.Length - 2) % 5 != 0)
            throw Invalid();
        if (!palettes.TryGetValue(payload[0], out var palette))
            palettes[payload[0]] = palette = new Palette(payload[1]);
        palette.Version = payload[1];
        for (var i = 2; i < payload.Length; i += 5)
            palette.Entries[payload[i]] = payload.Slice(i, 5).ToArray();
    }

    private void ReadObject(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
            throw Invalid();
        var id = BinaryPrimitives.ReadUInt16BigEndian(payload);
        if ((payload[3] & 0x80) != 0)
        {
            if (payload.Length < 11)
                throw Invalid();
            var expected = payload[4] << 16 | payload[5] << 8 | payload[6];
            if (expected < 4 || expected > MaximumStateBytes)
                throw Invalid();
            if (objects.Remove(id, out var previous))
                objectBytes -= previous.Segments.Sum(segment => segment.Length);
            objects[id] = new GraphicObject(payload[2], expected);
        }
        // 定位可能落在对象的续片上；继续回看直到找到其首片。
        if (!objects.TryGetValue(id, out var graphic) || graphic.Version != payload[2])
            return;
        objectBytes = checked(objectBytes + payload.Length);
        if (objectBytes > MaximumStateBytes)
            throw Invalid();
        graphic.Segments.Add(payload.ToArray());
        graphic.Received += payload.Length - ((payload[3] & 0x80) != 0 ? 7 : 4);
        graphic.Complete = (payload[3] & 0x40) != 0 && graphic.Received == graphic.Expected;
    }

    // null 表示回看范围内没有足够的状态；空数组表示文件起点之后尚无字幕。
    public byte[]? CreateEpoch(bool fromBeginning)
    {
        if (presentation is null)
            return fromBeginning ? [] : null;
        if ((!hasEpoch && !fromBeginning) || pendingDisplaySet || objects.Values.Any(graphic => !graphic.Complete))
            return null;
        if (presentation[10] > 0 && !palettes.ContainsKey(presentation[9]))
            return null;
        var cursor = 11;
        for (var i = 0; i < presentation[10]; i++)
        {
            if (presentation.Length - cursor < 8)
                throw Invalid();
            var id = BinaryPrimitives.ReadUInt16BigEndian(presentation.AsSpan(cursor));
            if (!objects.TryGetValue(id, out var graphic) || !graphic.Complete || !windows.ContainsKey(presentation[cursor + 2]))
                return null;
            cursor += (presentation[cursor + 3] & 0x80) != 0 ? 16 : 8;
            if (cursor > presentation.Length)
                throw Invalid();
        }
        if (cursor != presentation.Length)
            throw Invalid();
        if (windows.Count > byte.MaxValue)
            throw Invalid();
        using var output = new MemoryStream();
        var header = presentation.ToArray();
        header[7] = 0x80;
        header[8] = 0;
        WriteSegment(output, 0x16, header);
        // 清屏后仍要保留缓存，后续的普通显示集合可能继续复用旧对象。
        var windowData = new[] { checked((byte)windows.Count) }.Concat(windows.Keys
            .Order().SelectMany(id => windows[id])).ToArray();
        if (windows.Count > 0)
            WriteSegment(output, 0x17, windowData);
        foreach (var (id, palette) in palettes.OrderBy(pair => pair.Key))
            WriteSegment(output, 0x14, new[] { id, palette.Version }
                .Concat(palette.Entries.Values.SelectMany(entry => entry)).ToArray());
        foreach (var id in objects.Keys.Order())
        foreach (var segment in objects[id].Segments)
            WriteSegment(output, 0x15, segment);
        WriteSegment(output, 0x80, []);
        return output.ToArray();
    }

    public byte[] CreateClear()
    {
        if (presentation is null)
            return [];
        var header = presentation.AsSpan(0, 11).ToArray();
        var number = unchecked((ushort)(BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(5)) + 1));
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(5), number);
        header[7] = header[8] = header[10] = 0;
        using var output = new MemoryStream();
        WriteSegment(output, 0x16, header);
        WriteSegment(output, 0x80, []);
        return output.ToArray();
    }

    private static void WriteSegment(Stream output, byte kind, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[3];
        header[0] = kind;
        BinaryPrimitives.WriteUInt16BigEndian(header[1..], checked((ushort)payload.Length));
        output.Write(header);
        output.Write(payload);
    }

    private static RemuxExportException Invalid() => new(RemuxExportError.UnsupportedTrack, "PGS display set");
}
