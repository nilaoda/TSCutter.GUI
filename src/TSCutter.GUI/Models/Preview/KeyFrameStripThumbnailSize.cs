using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TSCutter.GUI.Models.Preview;

/// <summary>主窗口导航条每张缩略图的大小；选项顺序与设置窗口的下拉框一致。</summary>
public enum KeyFrameStripThumbnailSize
{
    Small,
    Medium,
    Large
}

/// <summary>
/// 导航条按缩略图大小确定的布局与解码尺寸。<see cref="Pitch"/> 决定可见数量（条宽除以它），
/// 每格实际宽度在 Pitch 与 <see cref="MaxTileWidth"/> 之间；图片区域高度为 TileHeight − 24（上下边距与时间文字），
/// 时间文字字号不随大小变化。解码尺寸与图片区域同比例缩放，保持各档在高分屏上的清晰度一致。
/// </summary>
internal readonly record struct KeyFrameStripLayout(double Pitch, double MaxTileWidth, double TileHeight,
    double Height, int DecodeWidth, int DecodeHeight)
{
    public static KeyFrameStripLayout Small { get; } = new(84, 88, 61, 68, 240, 135);
    public static KeyFrameStripLayout Medium { get; } = new(108, 112, 73, 80, 320, 180);
    public static KeyFrameStripLayout Large { get; } = new(160, 166, 98, 105, 480, 270);

    public static KeyFrameStripLayout For(KeyFrameStripThumbnailSize size) => size switch
    {
        KeyFrameStripThumbnailSize.Small => Small,
        KeyFrameStripThumbnailSize.Large => Large,
        _ => Medium
    };
}

/// <summary>读取配置时，未知的名称或数值回落到“中”，不让整个配置文件读取失败。</summary>
internal sealed class KeyFrameStripThumbnailSizeJsonConverter : JsonConverter<KeyFrameStripThumbnailSize>
{
    public override KeyFrameStripThumbnailSize Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String &&
            Enum.TryParse<KeyFrameStripThumbnailSize>(reader.GetString(), ignoreCase: true, out var named))
            return Normalize(named);
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number))
            return Normalize((KeyFrameStripThumbnailSize)number);
        reader.Skip();
        return KeyFrameStripThumbnailSize.Medium;
    }

    public override void Write(Utf8JsonWriter writer, KeyFrameStripThumbnailSize value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Normalize(value).ToString());

    internal static KeyFrameStripThumbnailSize Normalize(KeyFrameStripThumbnailSize value) =>
        Enum.IsDefined(value) ? value : KeyFrameStripThumbnailSize.Medium;
}
