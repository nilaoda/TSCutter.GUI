using System;
using System.Collections.Generic;

namespace TSCutter.GUI.Utils;

/// <summary>
/// 按数字段的实际数值比较字符串，使 2.ts 排在 10.ts 之前。
/// </summary>
internal sealed class NaturalStringComparer : IComparer<string>
{
    public static NaturalStringComparer Instance { get; } = new();

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left is null)
            return -1;
        if (right is null)
            return 1;

        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            var leftIsDigit = char.IsAsciiDigit(left[leftIndex]);
            var rightIsDigit = char.IsAsciiDigit(right[rightIndex]);
            if (leftIsDigit && rightIsDigit)
            {
                var result = CompareNumber(left, ref leftIndex, right, ref rightIndex);
                if (result != 0)
                    return result;
                continue;
            }

            var leftCharacter = char.ToUpperInvariant(left[leftIndex]);
            var rightCharacter = char.ToUpperInvariant(right[rightIndex]);
            if (leftCharacter != rightCharacter)
                return leftCharacter.CompareTo(rightCharacter);
            leftIndex++;
            rightIndex++;
        }

        return left.Length - leftIndex - (right.Length - rightIndex);
    }

    private static int CompareNumber(
        string left,
        ref int leftIndex,
        string right,
        ref int rightIndex)
    {
        var leftStart = leftIndex;
        var rightStart = rightIndex;
        while (leftIndex < left.Length && char.IsAsciiDigit(left[leftIndex]))
            leftIndex++;
        while (rightIndex < right.Length && char.IsAsciiDigit(right[rightIndex]))
            rightIndex++;

        var leftSignificant = leftStart;
        var rightSignificant = rightStart;
        while (leftSignificant < leftIndex && left[leftSignificant] == '0')
            leftSignificant++;
        while (rightSignificant < rightIndex && right[rightSignificant] == '0')
            rightSignificant++;

        var leftDigits = leftIndex - leftSignificant;
        var rightDigits = rightIndex - rightSignificant;
        if (leftDigits != rightDigits)
            return leftDigits.CompareTo(rightDigits);

        for (var index = 0; index < leftDigits; index++)
        {
            var difference = left[leftSignificant + index] - right[rightSignificant + index];
            if (difference != 0)
                return difference;
        }

        // 数值相同时使用前导零数量稳定区分，避免不同平台出现随机顺序。
        return (leftIndex - leftStart).CompareTo(rightIndex - rightStart);
    }
}
