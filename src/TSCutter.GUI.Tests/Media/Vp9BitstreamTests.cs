using TSCutter.GUI.Utils.Media;
using Xunit;

namespace TSCutter.GUI.Tests.Media;

/// <summary>
/// VP9 衔接点识别：样本（或超帧中的第一帧）是关键帧时可以从这里开始解码，包括在线视频站素材中常见的
/// “隐藏关键帧 + 只参考它的显示帧”超帧；帧间画面、show_existing_frame、超帧中第一帧为帧间画面时都不是衔接点。
/// </summary>
public sealed class Vp9BitstreamTests
{
    // 帧头首字节：frame_marker(2)=2、profile 低/高位、show_existing_frame、frame_type（0 为关键帧）、show_frame。
    private const byte ShownKey = 0b1000_0010;
    private const byte HiddenKey = 0b1000_0000;
    private const byte ShownInter = 0b1000_0110;
    private const byte HiddenInter = 0b1000_0100;
    private const byte ShowExisting = 0b1000_1000;
    // Profile 2（10 位）的显示关键帧：profile_low_bit = 0、profile_high_bit = 1。
    private const byte Profile2Key = 0b1001_0010;

    /// <summary>按超帧格式拼接：各帧之后是索引（标记字节、每帧 1 字节长度、标记字节）。</summary>
    private static byte[] Superframe(params byte[][] frames)
    {
        var marker = (byte)(0xC0 | (frames.Length - 1));
        return [.. frames.SelectMany(frame => frame), marker, .. frames.Select(frame => (byte)frame.Length), marker];
    }

    [Theory]
    [InlineData(ShownKey, true)]
    [InlineData(HiddenKey, true)]
    [InlineData(Profile2Key, true)]
    [InlineData(ShownInter, false)]
    [InlineData(ShowExisting, false)]
    [InlineData(0b0000_0010, false)]
    public void SingleFrameSamples(byte header, bool expected) =>
        Assert.Equal(expected, Vp9Bitstream.IsKeyFrame([header, 0x49, 0x83, 0x42, 0x00]));

    [Fact]
    public void SuperframesAreJudgedByTheirFirstFrame()
    {
        Assert.True(Vp9Bitstream.IsKeyFrame(Superframe([HiddenKey, 0x49, 0x83], [ShownInter, 0x11])));
        Assert.False(Vp9Bitstream.IsKeyFrame(Superframe([HiddenInter, 0x11, 0x22], [ShownInter, 0x33])));
        // 超帧索引的长度超出样本时不按超帧解析，也不当作关键帧。
        Assert.False(Vp9Bitstream.IsKeyFrame([HiddenKey, 0x49, 0xC1, 0x7F, 0x01, 0xC1]));
        Assert.False(Vp9Bitstream.IsKeyFrame([]));
    }
}
