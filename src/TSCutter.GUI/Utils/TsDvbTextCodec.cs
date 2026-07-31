using System;
using System.Linq;
using System.Text;

namespace TSCutter.GUI.Utils;

internal static class TsDvbTextCodec
{
    private static readonly Encoding Gb18030Encoding = CreateGb18030Encoding();

    public static string Decode(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
            return string.Empty;

        // DVB 文本以首字节选择字符集；优先覆盖广播中常见的 UTF-8/UTF-16，
        // 未声明字符集时按 ISO-8859-1 安全回退，无法识别的控制字符不会进入文件名或界面。
        Encoding encoding = Encoding.Latin1;
        var offset = 0;
        if (value[0] == 0x15)
        {
            encoding = Encoding.UTF8;
            offset = 1;
        }
        else if (value[0] == 0x11)
        {
            encoding = Encoding.BigEndianUnicode;
            offset = 1;
        }
        else if (value[0] is >= 0x01 and <= 0x0B)
        {
            var isoPart = value[0] switch
            {
                0x01 => 5,
                0x02 => 6,
                0x03 => 7,
                0x04 => 8,
                0x05 => 9,
                0x06 => 10,
                0x07 => 11,
                0x09 => 13,
                0x0A => 14,
                0x0B => 15,
                _ => 0
            };
            if (isoPart > 0)
                encoding = Encoding.GetEncoding($"ISO-8859-{isoPart}");
            offset = 1;
        }
        else if (value[0] == 0x10 && value.Length >= 3)
        {
            if (value[1] == 0 && value[2] is >= 1 and <= 15 && value[2] != 12)
                encoding = Encoding.GetEncoding($"ISO-8859-{value[2]}");
            offset = 3;
        }

        var textBytes = value[offset..];
        var text = encoding.GetString(textBytes).Trim();
        var highByteCount = 0;
        foreach (var item in textBytes)
        {
            if (item >= 0x80)
                highByteCount++;
        }
        if (offset == 0 && highByteCount >= 2)
        {
            var gbText = Gb18030Encoding.GetString(textBytes).Trim();
            // 国内 DVB 前端常把未带字符集标识的 GB2312/GBK 直接放入 SDT。
            // 仅在解码结果明确包含多个中日韩字符时采用 GB18030，避免影响标准拉丁文本。
            if (gbText.Count(IsCjkCharacter) >= 2)
                text = gbText;
        }
        return string.Concat(text.Where(character => !char.IsControl(character)));
    }

    public static byte[] Encode(string value)
    {
        if (string.IsNullOrEmpty(value))
            return [];
        var text = Encoding.UTF8.GetBytes(value);
        var result = new byte[text.Length + 1];
        result[0] = 0x15;
        text.CopyTo(result, 1);
        return result;
    }

    private static Encoding CreateGb18030Encoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(
            "GB18030", EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    }

    private static bool IsCjkCharacter(char value) => value is >= '\u3400' and <= '\u9FFF';
}
