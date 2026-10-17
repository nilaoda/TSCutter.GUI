# 原生标题栏行为核对

自绘主题与宿主系统版本需要分别处理：选择 Windows 11 配色并不代表系统支持 DWM 圆角或 Snap Layouts。

| 主题 / 条件 | 本项目的标题按钮提示策略 |
| --- | --- |
| Classic | 最小化、最大化、关闭均显示文字提示，沿用 Classic 提示框外观 |
| XP / Luna | 保留文字提示，随配色和语言更新 |
| Windows 7 / Aero | 保留文字提示，随配色和语言更新 |
| Windows 8 | 保留文字提示 |
| Windows 10 | 保留文字提示；现代 Windows 没有统一取消标题按钮提示 |
| Windows 11，普通按钮 | 最小化、关闭继续显示文字提示 |
| Windows 11，系统 Build 22000+，可缩放且可最大化 | 最大化按钮交给 `HTMAXBUTTON` / Snap Layouts，取消应用自身提示，避免与系统悬浮层重叠 |
| Windows 11，旧系统或不满足 Snap 条件 | 最大化按钮显示普通文字提示 |

最大化后提示改为还原，最小化按钮对应的窗口状态为最小化时也提示还原。以上应用文字支持英文、简体中文和繁体中文。

| 宿主系统 | Aero / Windows 11 窗口外框 |
| --- | --- |
| Windows，Build 22000 以下 | Windows 7 / Aero 与 Windows 11 主题统一使用直角窗口，包含浅色和深色；窗口表面和自绘细外框同时取消圆角，与原生矩形边框、阴影一致；`BorderOnly` 避免原生标题栏闪现，保留原生缩放边框，回调仅补回 `WS_SYSMENU`；显式启用 DWM 非客户区绘制，不再使用 `SetWindowRgn` 或应用圆角裁剪；材质仍按系统实际支持情况回退，关闭按钮悬停背景覆盖自绘细外框 |
| Windows，Build 22000 及以上 | 延续 DWM 圆角、材质和阴影处理 |
| macOS | 延续原生图层圆角处理 |

旧系统的 `Full` 装饰保留了原生标题栏绘制路径，最大化时可能短暂露出系统样式。`BorderOnly` 与完全移除装饰不同，Avalonia 在可缩放窗口上保留 `WS_THICKFRAME`；并通过样式回调保留 `WS_SYSMENU`，使 Alt+Space 和任务栏系统菜单继续可用；最大化消息、工作区计算和窗口操作仍由平台处理。

Windows 标题栏空白区域的右键菜单直接使用当前 HWND 的 `GetSystemMenu`，经 `TrackPopupMenuEx` 显示，并发送 `WM_SYSCOMMAND` 执行系统操作。菜单的外观、文字、快捷键和移动 / 大小交互均由宿主 Windows 提供，因此在 Windows 11 上选择 Aero 主题时，窗口菜单仍是当前系统的原生外观。macOS 等非 Windows 平台继续使用应用菜单。

显示前同步还原、移动、大小、最小化、最大化和关闭的可用状态，遵循固定窗口、对话框及标题按钮限制；NoChrome 对原生关闭项的禁用仅在菜单显示期间按实际按钮状态修正，结束后恢复原状态。关闭命令仍经过平台的窗口关闭流程，允许应用取消关闭。正文和标题按钮的右键保持原有行为。

## 资料与验证范围

- [Microsoft 的 GetSystemMenu](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getsystemmenu) 与 [TrackPopupMenuEx](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-trackpopupmenuex)：复用系统拥有的窗口菜单，选择后分发窗口系统命令。
- [Microsoft 的 Win32 提示框说明](https://learn.microsoft.com/en-us/windows/win32/uxguide/ctrl-tooltips-and-infotips)：图形按钮使用简短文字提示。
- [WinUI #10438](https://github.com/microsoft/microsoft-ui-xaml/issues/10438)：Windows 10 的原生 Win32 / UWP 标题按钮仍有提示，且最大化后应更新为还原。
- [WinUI #11802](https://github.com/microsoft/microsoft-ui-xaml/issues/11802)：现代 Windows 的标题按钮提示仍由系统提供；讨论中确认 Windows 11 最大化按钮的系统提示通常由 Snap Layouts 覆盖。因此不能将最大化按钮上的现象推广到所有标题按钮。
- [Microsoft 的 DWMNCRENDERINGPOLICY 说明](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmncrenderingpolicy)：关闭非客户区渲染也会移除系统阴影，因此自绘期间使用 `DWMNCRP_ENABLED`，离开主题后恢复 `DWMNCRP_USEWINDOWSTYLE`。
- [Avalonia 11.3.12 WindowImpl](https://github.com/AvaloniaUI/Avalonia/blob/11.3.12/src/Windows/Avalonia.Win32/WindowImpl.cs)：`BorderOnly` 的样式处理保留可缩放窗口的原生边框；`Full` 装饰仍允许非客户区绘制。

兼容模式依据 Windows 10 Build 10240 的反馈采用直角窗口。自动化验证覆盖两个主题的浅深色、窗口状态切换、四角完整填充、退出兼容模式恢复圆角，以及 100%、125%、200% 缩放；另外验证标题按钮提示、Snap 标记、细外框与关闭悬停边缘像素。自动化测试在 macOS 的 Skia 无头环境运行，不能替代原生 DWM、Snap 悬浮层和系统菜单的实机验证。Windows 外观、旧系统直角兼容及原生标题栏右键菜单已通过用户的预览包验证。非 Windows 菜单回退已验证正文菜单、标题按钮、禁用项、键盘关闭、动态语言和关闭取消。
