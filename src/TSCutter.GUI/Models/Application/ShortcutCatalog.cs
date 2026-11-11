using System;
using System.Collections.Generic;
using System.Linq;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.Models.Application;

/// <summary>快捷键列表中的一行：操作说明与两类平台上的按键写法。</summary>
public sealed record ShortcutEntry(string Action, string WindowsKeys, string MacKeys)
{
    /// <summary>当前平台的按键写法；列表只显示这一列。</summary>
    public string Keys => KeysFor(OperatingSystem.IsMacOS());

    public string KeysFor(bool macOS) => macOS ? MacKeys : WindowsKeys;

    /// <summary>操作说明或指定平台的按键包含关键字时匹配（不区分大小写）。</summary>
    public bool Matches(string keyword, bool macOS) =>
        Action.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)
        || KeysFor(macOS).Contains(keyword, StringComparison.OrdinalIgnoreCase);
}

/// <summary>快捷键列表中的一个分类。</summary>
public sealed record ShortcutGroup(string Title, IReadOnlyList<ShortcutEntry> Entries);

/// <summary>
/// 设置窗口中只读快捷键列表的数据来源。键盘按键与主窗口的按键绑定一致；
/// 鼠标操作随滚轮设置变化，因此列表按当前（可能尚未保存的）设置生成。
/// 文字一律通过生成的本地化属性读取，资源键改名或删除时编译即会报错。
/// </summary>
public static class ShortcutCatalog
{
    private const string Pair = " / ";

    /// <param name="text">当前语言的文字。</param>
    /// <param name="input">用于生成鼠标操作说明的滚轮设置。</param>
    public static IReadOnlyList<ShortcutGroup> Build(LocalizationManager text, InputPreferences input)
    {
        var wheel = text.String_Shortcuts_Key_Wheel;
        var click = text.String_Shortcuts_Key_Click;
        var drag = text.String_Shortcuts_Key_Drag;
        var doubleClick = text.String_Shortcuts_Key_DoubleClick;
        var space = text.String_Shortcuts_Key_Space;
        // 按帧只在智能剪辑模式下生效，说明中注明其他模式按关键帧移动。
        var stripWheel = input.WheelStepUnit == WheelStepUnit.Frame
            ? text.String_Shortcuts_StripWheelFrames
            : text.String_Shortcuts_StripWheel;

        var mouse = new List<ShortcutEntry>
        {
            new(text.String_Shortcuts_StripClick, click, click),
            new(Format(stripWheel, StepText(text, input.WheelStepUnit, input.WheelStep)), wheel, wheel),
            new(Format(stripWheel, StepText(text, input.WheelStepUnit, input.ShiftWheelStep)),
                "Shift+" + wheel, "⇧+" + wheel)
        };
        if (input.PreviewWheelAction == PreviewWheelAction.Navigate)
        {
            mouse.Add(new(text.String_Shortcuts_PreviewNavigate, wheel, wheel));
            mouse.Add(new(text.String_Shortcuts_PreviewZoom, "Ctrl+" + wheel, "⌘+" + wheel));
        }
        else
            mouse.Add(new(text.String_Shortcuts_PreviewZoom, wheel, wheel));
        mouse.Add(new(text.String_Shortcuts_PreviewPan, drag, drag));
        mouse.Add(new(text.String_Shortcuts_ToggleFullScreen, doubleClick, doubleClick));
        mouse.Add(new(text.String_Shortcuts_TimelineSeek, click + Pair + drag, click + Pair + drag));
        mouse.Add(new(text.String_Shortcuts_TimelineZoom, "Ctrl+" + wheel, "⌘+" + wheel));
        mouse.Add(input.TimelineWheelAction == TimelineWheelAction.Pan
            ? new(text.String_Shortcuts_TimelinePan, wheel + Pair + "Shift+" + wheel, wheel + Pair + "⇧+" + wheel)
            : new(text.String_Shortcuts_TimelinePan, "Shift+" + wheel, "⇧+" + wheel));
        if (input.TimelineWheelAction == TimelineWheelAction.Navigate)
            mouse.Add(new(text.String_Shortcuts_TimelineNavigate, wheel, wheel));

        return
        [
            new(text.String_Shortcuts_Group_File,
            [
                new(text.String_Shortcuts_OpenVideo, "Ctrl+O", "⌘O"),
                new(text.String_Shortcuts_SaveClip, "Ctrl+S", "⌘S")
            ]),
            new(text.String_Shortcuts_Group_Edit,
            [
                new(text.String_Shortcuts_Undo, "Ctrl+Z", "⌘Z"),
                new(text.String_Shortcuts_Redo, "Ctrl+Y", "⇧⌘Z"),
                new(text.String_Shortcuts_MarkStart, "I", "I"),
                new(text.String_Shortcuts_MarkEnd, "O", "O")
            ]),
            new(text.String_Shortcuts_Group_Navigate,
            [
                new(text.String_Shortcuts_PreviousKeyFrame, "←", "←"),
                new(text.String_Shortcuts_NextKeyFrame, "→", "→"),
                new(text.String_Shortcuts_Back10KeyFrames, "Shift+←", "⇧←"),
                new(text.String_Shortcuts_Forward10KeyFrames, "Shift+→", "⇧→"),
                new(text.String_Shortcuts_PreviousFrame, ",", ","),
                new(text.String_Shortcuts_NextFrame, ".", ".")
            ]),
            new(text.String_Shortcuts_Group_Playback,
            [
                new(text.String_Shortcuts_TogglePlayback, space, space),
                new(text.String_Shortcuts_ToggleFullScreen, "F11", "⌃⌘F"),
                new(text.String_Shortcuts_ExitFullScreen, "Esc", "Esc")
            ]),
            new(text.String_Shortcuts_Group_FullScreen,
            [
                new(text.String_Shortcuts_FullScreenSeekSmall, "←" + Pair + "→", "←" + Pair + "→"),
                new(text.String_Shortcuts_FullScreenSeekLarge, "PgDn" + Pair + "PgUp", "Fn+↓" + Pair + "Fn+↑"),
                new(text.String_Shortcuts_FullScreenSeekEnds, "Home" + Pair + "End", "Fn+←" + Pair + "Fn+→"),
                new(text.String_Shortcuts_FullScreenVolume, "←" + Pair + "→", "←" + Pair + "→")
            ]),
            new(text.String_Shortcuts_Group_Mouse, mouse),
            new(text.String_Shortcuts_Group_Tools,
            [
                new(text.String_Shortcuts_PacketViewerJump, "Enter", "Return")
            ])
        ];
    }

    /// <summary>按移动单位写出“n 个关键帧 / n 帧 / n 秒”，供设置下拉框与快捷键说明共用。</summary>
    public static string StepText(LocalizationManager text, WheelStepUnit unit, int value) => unit switch
    {
        WheelStepUnit.Frame => value == 1 ? text.String_Settings_Shortcuts_Step_FrameOne
            : Format(text.String_Settings_Shortcuts_Step_Frames, value),
        WheelStepUnit.Second => value == 1 ? text.String_Settings_Shortcuts_Step_SecondOne
            : Format(text.String_Settings_Shortcuts_Step_Seconds, value),
        _ => value == 1 ? text.String_Settings_Shortcuts_Step_KeyFrameOne
            : Format(text.String_Settings_Shortcuts_Step_KeyFrames, value)
    };

    /// <summary>不带具体单位的数量（“n 个单位”），供设置中的步长下拉框使用，单位由“移动单位”决定。</summary>
    public static string UnitCountText(LocalizationManager text, int value) =>
        value == 1 ? text.String_Settings_Shortcuts_Step_UnitOne : Format(text.String_Settings_Shortcuts_Step_Units, value);

    /// <summary>
    /// 只保留匹配关键字的行；分类标题匹配时保留整个分类，没有匹配行的分类不显示。
    /// 按键只按显示的平台匹配，避免搜到的行显示的是另一种写法。
    /// </summary>
    public static IReadOnlyList<ShortcutGroup> Filter(IReadOnlyList<ShortcutGroup> groups, string? keyword) =>
        Filter(groups, keyword, OperatingSystem.IsMacOS());

    public static IReadOnlyList<ShortcutGroup> Filter(IReadOnlyList<ShortcutGroup> groups, string? keyword, bool macOS)
    {
        keyword = keyword?.Trim();
        if (string.IsNullOrEmpty(keyword))
            return groups;
        return groups
            .Select(group => group.Title.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)
                ? group
                : group with { Entries = group.Entries.Where(entry => entry.Matches(keyword, macOS)).ToArray() })
            .Where(group => group.Entries.Count > 0)
            .ToArray();
    }

    // 译文中的占位符写错时仍显示原文，不让设置窗口因格式异常打不开。
    private static string Format(string format, object value)
    {
        try
        {
            return string.Format(format, value);
        }
        catch (FormatException)
        {
            return format;
        }
    }
}
