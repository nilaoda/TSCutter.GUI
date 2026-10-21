using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace TSCutter.GUI.Models.Editing;

public static class ClipLabels
{
    public const int MaximumNameLength = 120;
    public const int MaximumNotesLength = 2000;

    public static bool IsValid(string? name, string? notes, string? color) =>
        name is { Length: <= MaximumNameLength } && notes is { Length: <= MaximumNotesLength } &&
        color is "" or "blue" or "green" or "amber" or "red" or "purple";

    private static readonly IBrush Blue = new ImmutableSolidColorBrush(Color.Parse("#4285D4"));
    private static readonly IBrush Green = new ImmutableSolidColorBrush(Color.Parse("#47A878"));
    private static readonly IBrush Amber = new ImmutableSolidColorBrush(Color.Parse("#D59B32"));
    private static readonly IBrush Red = new ImmutableSolidColorBrush(Color.Parse("#D95E62"));
    private static readonly IBrush Purple = new ImmutableSolidColorBrush(Color.Parse("#A079CD"));
    public static IBrush Brush(string? color) => color switch
    {
        "blue" => Blue,
        "green" => Green,
        "amber" => Amber,
        "red" => Red,
        "purple" => Purple,
        _ => Brushes.Transparent
    };
}
