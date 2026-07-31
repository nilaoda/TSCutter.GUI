using System.Globalization;

namespace TSCutter.GUI.Utils;

internal static class TsNumberParser
{
    public static bool TryParse(string? text, out int value)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            value = 0;
            return false;
        }
        return text.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
