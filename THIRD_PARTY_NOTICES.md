# 第三方声明 / Third-party notices

TSCutter.GUI 以 GPL v3 发布。本声明与上游许可原文在发布打包时由独立脚本合并为一个 `THIRD_PARTY_NOTICES.txt`，随发布包提供；各许可原文位于该文件下方。

TSCutter.GUI is distributed under GPL v3. This notice and original upstream license texts are combined into a single THIRD_PARTY_NOTICES.txt by a standalone script during release packaging and included with the release. Original license texts follow below.

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
| [libass 0.17.4](https://github.com/libass/libass/tree/0.17.4) | ISC |
| [libbluray 1.5.0](https://code.videolan.org/videolan/libbluray) | LGPL-2.1-or-later |
| [miniaudio 0.11.25](https://github.com/mackron/miniaudio) | Unlicense / MIT |
| [libdvdread 7.1.1](https://code.videolan.org/videolan/libdvdread) | GPL-2.0-or-later |
| [libdvdnav 7.0.0](https://code.videolan.org/videolan/libdvdnav) | GPL-2.0-or-later |
| [libdvdcss 1.6.0](https://code.videolan.org/videolan/libdvdcss) | GPL-2.0-or-later |
| [libudfread 1.2.0](https://code.videolan.org/videolan/libudfread) | LGPL-2.1-or-later |
| [Splat](https://github.com/reactiveui/splat) | MIT |
| [Tmds.DBus.Protocol](https://github.com/tmds/Tmds.DBus) | MIT |

字体使用 [Inter](https://github.com/rsms/inter)（SIL OFL 1.1）；共享运行时来自 [FFmpegSharedLibraries](https://github.com/nilaoda/FFmpegSharedLibraries)（GPL v3）。本清单不展开各项目内部的依赖。

The font is Inter (SIL OFL 1.1); the shared runtime comes from FFmpegSharedLibraries (GPL v3). Internal dependencies are not listed individually.

字幕渲染静态合入 avfilter，包含 libass 0.17.4、FreeType 2.13.3、HarfBuzz 10.2.0、FriBidi 1.0.16、libunibreak 6.1；Linux 还包含 Fontconfig 2.16.0 和 Expat 2.6.4。源代码、版本及构建补丁见 [FFmpegSharedLibraries](https://github.com/nilaoda/FFmpegSharedLibraries)。各项目的上游许可原文随本声明附带。

光盘镜像输入使用静态合入 avformat 的 libbluray、libdvdread、libdvdnav、libdvdcss 和 libudfread。应用从 avformat 调用 libbluray / libdvdread 的公开接口，蓝光按播放列表读取与定位，DVD 由 dvdvideo 解复用器读取；DVD 的 CSS 保护由 libdvdcss 处理；暂不支持蓝光 AACS／BD+，不捆绑 libaacs / libbdplus。上游许可原文随本声明附带；libdvdread、libdvdnav 和 libdvdcss 为 GPL-2.0-or-later，与本项目 GPL-3.0 兼容。

Disc image input uses libbluray, libdvdread, libdvdnav, libdvdcss and libudfread statically embedded in avformat. The application calls the public libbluray/libdvdread APIs through avformat to read and seek Blu-ray playlists and enumerate DVD titles; DVD input uses the dvdvideo demuxer. DVD CSS is handled by libdvdcss. Protected Blu-ray input remains unsupported, and no libaacs or libbdplus is bundled. Upstream license texts are included; libdvdread, libdvdnav and libdvdcss are GPL-2.0-or-later, which is compatible with this project's GPL-3.0.

音频设备输出桥 `tscutter_audio` 由随应用构建编译的 `native/audio/tsaudio_bridge.c` 与 vendored 的 miniaudio 0.11.25 组成，只负责把已解码并重采样的交错 float32 PCM 写入默认音频设备（WASAPI / CoreAudio / ALSA），解复用、解码、下混与重采样仍由应用的 FFmpeg 共享运行库完成。AOT 发布将音频桥静态链接进应用，普通构建与非 AOT 发布使用动态桥。许可原文随本声明附带。

The audio output bridge `tscutter_audio` is built with the app from `native/audio/tsaudio_bridge.c` and vendored miniaudio 0.11.25. It only writes decoded, resampled interleaved float32 PCM to the default audio device (WASAPI / CoreAudio / ALSA); demuxing, decoding, downmixing and resampling stay in the app's FFmpeg shared runtime. AOT releases statically link the bridge into the application; normal builds and non-AOT publishing use a shared bridge. The original license text is included with this notice.

Subtitle rendering is linked statically into avfilter and includes libass 0.17.4, FreeType 2.13.3, HarfBuzz 10.2.0, FriBidi 1.0.16 and libunibreak 6.1; Linux also includes Fontconfig 2.16.0 and Expat 2.6.4. Source versions and build patches are available in [FFmpegSharedLibraries](https://github.com/nilaoda/FFmpegSharedLibraries). Original upstream license texts are included with this notice. FriBidi uses LGPL 2.1 or later; the complete LGPL 2.1 text is included among the consolidated licenses.

## 参考代码 / Adapted code

- **FFmpeg.AutoGen**：`FFmpeg/NativeMethods.cs` 的接口签名参考其[生成绑定](https://github.com/Ruslan-B/FFmpeg.AutoGen/tree/7ec3390be7216bd1f8482b824f90c27cb601cd89)，改为 `LibraryImport`，swscale 数组参数改为原生指针。/ NativeMethods signatures are adapted from the generated bindings to use LibraryImport and native pointers for swscale arrays.
- **Sdcb.FFmpeg**：Copyright © sdcb, Ruslan Balanukhin 2022，LGPL-3.0-only。`FFmpeg/VideoFrameConverter.cs` 参考其[原始实现](https://github.com/sdcb/Sdcb.FFmpeg/tree/0cf0c27bd51427c9203b1cd3b6af78686f1b0c08)，改为直接传递原生帧的平面和步长；NuGet 依赖已移除。原始代码保存在源码仓库的 `design/reference/Sdcb.FFmpeg.VideoFrameConverter.cs.txt`，修改日期为 2026-10-04。/ VideoFrameConverter adapts the original implementation to pass native frame planes and strides directly. The NuGet dependency has been removed. Original code is preserved at design/reference/Sdcb.FFmpeg.VideoFrameConverter.cs.txt in the source repository; modified on 2026-10-04.

- **libplacebo**（LGPL-2.1-or-later）：`Rendering/Colors` 中 P5 重建曲线、MMR、颜色矩阵解释，以及 IPT 色调和感知色域映射参考 [v7.351.0 的实现](https://github.com/haasn/libplacebo/tree/v7.351.0/src)，改为 C# CPU/SIMD 与 GPU 着色器处理，并加入 SDR 色调及色域映射。修改日期为 2026-10-30；不依赖 libplacebo 动态库。/ P5 reconstruction, metadata interpretation, IPT tone mapping and perceptual gamut mapping are adapted to C# CPU/SIMD processing and GPU shaders; modified on 2026-10-30. No libplacebo runtime is distributed.
