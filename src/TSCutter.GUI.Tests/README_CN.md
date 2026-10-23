# 运行测试

[English](README.md) | 简体中文

Windows、macOS 和 Linux 使用同一个测试项目，需要 .NET 10 SDK。

```sh
dotnet test src/TSCutter.GUI.Tests -c Debug
```

普通逻辑、NAL 类型解析和无头 UI 测试不需要视频素材，也不需要配置环境变量。
原生媒体集成测试缺少前提时会显示 **Skipped**，并提示缺少的是素材还是 FFmpeg 运行库，以及具体的准备方法。

## 一次准备合成素材

安装 Python 3，以及带 `libx264`、`libx265`、`dvbsub` 编码器的 FFmpeg CLI。确保 `ffmpeg` 和 `ffprobe` 在 PATH 中，从仓库根目录运行：

```sh
python tools/prepare-test-media.py
dotnet test src/TSCutter.GUI.Tests -c Debug
```

这两条命令同样适用于 Windows PowerShell；Python 命令在 macOS/Linux 上也可以写成 `python3`。
如果工具不在 PATH 中，可以显式指定路径，带空格的路径要加引号。例如 Windows：

```powershell
python tools/prepare-test-media.py --ffmpeg "D:\Media Tools\ffmpeg.exe" --ffprobe "D:\Media Tools\ffprobe.exe"
```

默认生成到 `artifacts/test-media/`，该目录已被 Git 忽略，不提交视频。构建测试项目时会将生成结果复制到测试输出目录，测试自动读取，无需设置素材变量。准备脚本失败时不要继续运行集成测试；修复工具或编码器缺失的问题后重新生成。

只准备其中一组也可以：

```sh
python tools/prepare-test-media.py --group keyframes
python tools/prepare-test-media.py --group remux subtitles
```

| 组 | 覆盖内容 |
| --- | --- |
| `keyframes` | H.264/HEVC、固定和变长 GOP、密集 GOP、B 帧、分数帧率、非零 PTS 的 TS、HLG、短片和单帧 |
| `remux` | MP4/MKV 导出、多音轨、字幕、章节、附件、封面、HDR 和 open GOP |
| `subtitles` | mov_text、ASS 动画、PGS 和 DVB 字幕的预览、定位和切换 |

三组生成器也能单独运行；不传目录时使用相同的默认位置。`--font` 可以为字幕附件测试指定 TTF/OTF 字体。
生成脚本只负责测试数据，不下载工具，不修改应用配置，也不会在每次 `dotnet test` 时重新编码。

## FFmpeg 运行库

生成素材使用 `ffmpeg` / `ffprobe` 命令行工具；C# 解码测试加载的是应用使用的 **FFmpeg 9 共享库**，仅有 `ffmpeg.exe` 并不能代替它。

从 [FFmpegSharedLibraries](https://github.com/nilaoda/FFmpegSharedLibraries) 准备与当前系统和测试进程架构匹配的完整运行库。自动查找位置包括：

- 测试输出目录：`src/TSCutter.GUI.Tests/bin/Debug/net10.0/`。
- 应用输出目录：`src/TSCutter.GUI/bin/Debug/net10.0/`，也支持带 RID 的子目录。
- `TSCUTTER_FFMPEG_ROOT` / `FFMPEG_ROOT` 指定的目录。
- macOS/Linux 上应用本身支持的系统库目录。

Release 测试对应查找 `Release` 输出目录。Windows 使用 DLL，macOS 使用 dylib，Linux 使用 so。请保留完整包及其依赖，不能混用操作系统、CPU 架构或 ABI 版本。

已经为本机应用准备了当前配置的运行库时，测试会直接复用。如果运行库在其他位置，Windows PowerShell 可这样指定：

```powershell
$env:TSCUTTER_FFMPEG_ROOT = "D:\Tools\ffmpeg-runtime"
dotnet test src/TSCutter.GUI.Tests -c Debug
```

macOS/Linux 对应：

```sh
TSCUTTER_FFMPEG_ROOT=/path/to/ffmpeg-runtime dotnet test src/TSCutter.GUI.Tests -c Debug
```

找到库文件后，如果加载失败、架构或 ABI 不匹配，测试会失败并显示加载错误，不会把损坏的运行库当成正常跳过。

## 可选的自定义目录和真实素材

默认的合成测试不依赖下面的环境变量。它们用于复用已有目录或运行无法通用生成的真实素材检查：

| 变量 | 用途 |
| --- | --- |
| `TSCUTTER_TEST_MEDIA_ROOT` | 可选的合成素材根目录，内含 `keyframes/`、`remux/`、`subtitles/`；与准备脚本的 `--output-dir` 配合使用 |
| `TSCUTTER_REMUX_TEST_SAMPLES` | 兼容旧的封装导出素材目录，优先于公共根目录 |
| `TSCUTTER_SUBTITLE_TEST_SAMPLES` | 兼容旧的字幕素材目录，优先于公共根目录 |
| `TSCUTTER_FFMPEG_TEST_SAMPLES` | 可选的真实编码样本目录：`1080i.ts`、`4K.ts`、`AVS+.ts`、`AVS+_2.ts`、`AVS+_P.ts`、`AVS2.ts`、`AVS3.ts`；检查特殊解码器的位置元数据等行为 |
| `TSCUTTER_DOVI_TEST_SAMPLE` | 可选的 Dolby Vision Profile 5 MP4，视频轨道 0、音频轨道 1，用于导出和 HDR 预览检查 |
| `TSCUTTER_AV3A_TEST_SAMPLE` | 可选的 Audio Vivid MP4，用于 AV3A 导出检查 |
| `TSCUTTER_SUBTITLE_REAL_SAMPLE` | 可选的 MP4，恰有一个字幕轨道且 2.5 秒处有活动字幕 |

真实素材不下载、不生成、不入库。未配置时只跳过对应的真实素材用例，不影响合成测试和普通测试。
关键帧测试使用公共的 `keyframes` 目录，不需要单独的素材目录变量。
`TSCUTTER_KEYFRAME_STRIP_CAPTURE`、`TSCUTTER_SUBTITLE_PREVIEW_IMAGE` 只是可选的截图输出位置，不是测试运行前提。
