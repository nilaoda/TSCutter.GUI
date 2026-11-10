using TSCutter.GUI.Utils.TransportStream.Packets;
using Xunit;

namespace TSCutter.GUI.Tests.TransportStream.Packets;

public sealed class TsDvbTextCodecTests
{
    [Theory]
    [InlineData("15E6B58BE8AF95", "测试")]
    [InlineData("13D6D0CEC4BDDAC4BF", "中文节目")]
    [InlineData("D6D0CEC4BDDAC4BF", "中文节目")]
    [InlineData("436166E9", "Café")]
    public void DecodesCommonBroadcastText(string hex, string expected)
    {
        Assert.Equal(expected, TsDvbTextCodec.Decode(Convert.FromHexString(hex)));
    }

    [Fact]
    public void DoesNotGuessAribTextAsGb18030()
    {
        // 含转义、换集控制的 ARIB 文本沿用安全回退，不能因 GBK 猜测生成私用区字符。
        var text = TsDvbTextCodec.Decode(Convert.FromHexString("1B7CB9DDF9C4E9A4D60E2B"));
        Assert.Equal("|¹ÝùÄé¤Ö+", text);
    }
}
