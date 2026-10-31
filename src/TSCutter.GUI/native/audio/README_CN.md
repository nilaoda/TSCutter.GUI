# 原生音频输出桥

[English](README.md) | 简体中文

`libtscutter_audio` 将交错 float32 PCM 送入默认音频设备。解复用、解码、下混和重采样
继续使用应用现有的 FFmpeg 运行库（含 swresample）。本桥不包含或编译 FFmpeg。

## 组成

| 文件 | 说明 |
| --- | --- |
| `miniaudio.h` | miniaudio 0.11.25，来源 https://github.com/mackron/miniaudio；Unlicense/MIT 双许可（见 `LICENSE-miniaudio.txt`） |
| `tsaudio_bridge.c` | SPSC 环形缓冲、设备回调及音频时钟所用的已播放帧计数 |

`tsaudio_*` C ABI 提供设备生命周期、PCM 写入、播放状态、已播放帧与欠载计数，以及
作用于已缓冲 PCM 的设备输出音量。原生设备回调执行有界拷贝，不调用托管代码。

## 构建

Windows、macOS 和 Linux 的 AOT 发布通过 `DirectPInvoke` 将音频桥静态链接进可执行文件，
发布目录不再需要单独的 `tscutter_audio.dll`、`libtscutter_audio.dylib` 或 `libtscutter_audio.so`。

普通 `dotnet build`、测试和非 AOT 发布使用动态桥，因为托管程序集无法静态链接原生代码。
构建会自动生成静态库和动态库，托管运行时只复制动态库。
目标架构由 `RuntimeIdentifier` 指定，未指定时采用 SDK 运行标识；中间产物在 `obj` 下按 RID 隔离。

- **macOS：**需要 Xcode 命令行工具（`clang`），通过 `-arch` 选择 x64 或 arm64。
- **Linux：**需要 C 编译器（`cc`）、归档工具（`ar`）以及 pthread、dl 和数学库。
  跨架构编译时，将 `AudioBridgeCompiler` 及必要时的 `AudioBridgeArchiver` 指定为对应目标架构的工具。
- **Windows：**需要 Visual Studio C++ 构建工具，由 `vswhere` 定位。
  构建过程按目标架构选择 `vcvarsall.bat` 参数，arm64 需要安装对应的 MSVC 工具链。

请在目标操作系统上构建。设置 `TSCutterSkipAudioBridge=true` 可跳过音频桥；
运行时没有可用音频桥则无声播放。打包版本的使用者无需安装编译器。

## 更新 miniaudio

同步替换 `miniaudio.h` 和 `LICENSE-miniaudio.txt`，并确认 `tsaudio_bridge.c` 使用的 API 仍兼容。
音频桥只启用设备输出、环形缓冲和原子操作，关闭 miniaudio 的解码、编码和音频引擎功能。
