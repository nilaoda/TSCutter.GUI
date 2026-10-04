# 第三方声明 / Third-party notices

TSCutter.GUI 以 GPL v3 发布。本声明与上游许可原文在构建时合并为一个 `THIRD_PARTY_NOTICES.txt`，随发布包提供；各许可原文位于该文件下方。

TSCutter.GUI is distributed under GPL v3. This notice and original upstream license texts are combined into a single THIRD_PARTY_NOTICES.txt during building and included with the release. Original license texts follow below.

## 主要项目 / Main projects

同一项目的多个 NuGet 包合并列出，具体版本见 `TSCutter.GUI.csproj`。

Multiple NuGet packages from the same project are grouped below. Versions are specified in `TSCutter.GUI.csproj`.

| 项目 / Project | 许可 / License |
| --- | --- |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) | MIT |
| [Avalonia.Xaml.Behaviors](https://github.com/wieslawsoltes/Xaml.Behaviors) | MIT |
| [Classic.Avalonia](https://github.com/BAndysc/Classic.Avalonia) | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MIT |
| [HanumanInstitute.MvvmDialogs](https://github.com/mysteryx93/HanumanInstitute.MvvmDialogs) | MIT |
| [FFmpeg.AutoGen.Abstractions](https://github.com/Ruslan-B/FFmpeg.AutoGen) | MIT |
| [Splat](https://github.com/reactiveui/splat) | MIT |
| [Tmds.DBus.Protocol](https://github.com/tmds/Tmds.DBus) | MIT |

字体使用 [Inter](https://github.com/rsms/inter)（SIL OFL 1.1）；共享运行时来自 [FFmpegSharedLibraries](https://github.com/nilaoda/FFmpegSharedLibraries)（GPL v3）。本清单不展开各项目内部的依赖。

The font is Inter (SIL OFL 1.1); the shared runtime comes from FFmpegSharedLibraries (GPL v3). Internal dependencies are not listed individually.

## 参考代码 / Adapted code

- **FFmpeg.AutoGen**：`FFmpeg/NativeMethods.cs` 的接口签名参考其[生成绑定](https://github.com/Ruslan-B/FFmpeg.AutoGen/tree/7ec3390be7216bd1f8482b824f90c27cb601cd89)，改为 `LibraryImport`，swscale 数组参数改为原生指针。/ NativeMethods signatures are adapted from the generated bindings to use LibraryImport and native pointers for swscale arrays.
- **Sdcb.FFmpeg**：Copyright © sdcb, Ruslan Balanukhin 2022，LGPL-3.0-only。`FFmpeg/VideoFrameConverter.cs` 参考其[原始实现](https://github.com/sdcb/Sdcb.FFmpeg/tree/0cf0c27bd51427c9203b1cd3b6af78686f1b0c08)，改为直接传递原生帧的平面和步长；NuGet 依赖已移除。原始代码保存在源码仓库的 `ThirdParty/Sdcb.FFmpeg/`，修改日期为 2026-10-04。/ VideoFrameConverter adapts the original implementation to pass native frame planes and strides directly. The NuGet dependency has been removed. Original code is preserved in the source repository's ThirdParty/Sdcb.FFmpeg directory; modified on 2026-10-04.
