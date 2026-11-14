# 运行测试

[English](README.md) | 简体中文

Windows、macOS 和 Linux 使用同一个测试项目，需要 .NET 11 SDK。

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
| `keyframes` | H.264/HEVC、固定和变长 GOP、密集 GOP、B 帧、分数帧率、非零 PTS 的 TS、HLG、IDR 与场景切换 CRA 混用且重排序深度为 4 的 HEVC TS、短片和单帧 |
| `remux` | MP4/MKV 导出、多音轨、文字/PGS 字幕、分数帧率 HEVC、章节、附件、封面、HDR 和 open GOP |
| `subtitles` | mov_text、ASS 动画、PGS 和 DVB 字幕的预览、局部定位、稀疏/空轨道和切换 |
| `frameaccurate` | 帧精确剪辑：画面内绘制帧序号，TS/M2TS/MP4/MKV 中带 B 帧的闭合 GOP H.264、带 RADL 的 10 位 HEVC（HLG/HDR10）、只有 CRA 关键帧且带 RASL 前导画面的开放 GOP HEVC（TS 与 MP4）、隔行开放 GOP MPEG-2、分数帧率、很大及回绕的 TS PTS、mov_text、H.264 High 4:2:2 与 HEVC 4:2:2（HEVC：带 RADL 的 10 位 TS、8 位 MP4；H.264：8 位；10 位开放 GOP、B 帧金字塔且 SPS 不声明重排序深度；隔行 MBAFF）、MP4/MKV 中的 AV1（8 位带 Opus 音频、10 位；含隐藏帧，每个关键帧带序列头）、MP4/MKV 中的 VP9（两遍编码的 8 位带 Opus 音频、10 位 Profile 2；含带隐藏替代参考帧的超帧）、PCM 音频未声明声道布局的 MOV、带接收中断的录制（PES 不完整、时间戳跳变 90 秒、参考帧缺失），只有普通 I 帧的开放 GOP H.264（CABAC、CAVLC、MBAFF，以及前导画面中有作参考 B 帧的 B 帧金字塔），以及应被拒绝的 4:4:4 素材 |

各组生成器也能单独运行；不传目录时使用相同的默认位置。`--font` 可以为字幕附件测试指定 TTF/OTF 字体。
生成脚本只负责测试数据，不下载工具，不修改应用配置，也不会在每次 `dotnet test` 时重新编码。

## 可选的光盘镜像测试

执行 `python tools/prepare-test-media.py --group iso` 生成合成 DVD 和蓝光镜像。
该组需要额外安装 `dvdauthor` / `spumux` 及支持 UDF 的 ISO 制作工具，因此默认不生成。
测试还需要带光盘支持的 FFmpeg 运行库，依赖和覆盖范围见[ISO 工具说明](../../tools/README_CN.md#光盘镜像iso测试素材)。

## FFmpeg 运行库

字幕转换测试还需要 SRT 和 mov_text 编码器；旧运行库缺少这些编码器时，仅跳过对应的转换和混流窗口测试。

生成素材使用 `ffmpeg` / `ffprobe` 命令行工具；C# 解码测试加载的是应用使用的 **FFmpeg 9 共享库**，仅有 `ffmpeg.exe` 并不能代替它。

从 [FFmpegSharedLibraries](https://github.com/nilaoda/FFmpegSharedLibraries) 准备与当前系统和测试进程架构匹配的完整运行库。自动查找位置包括：

- 测试输出目录：`src/TSCutter.GUI.Tests/bin/Debug/net11.0/`。
- 应用输出目录：`src/TSCutter.GUI/bin/Debug/net11.0/`，也支持带 RID 的子目录。
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

## 播放与音频设备

播放测试使用生成的 `keyframes` 和 `remux` 素材。多数音频测试使用内存输出，不会打开扬声器。
三项真实设备测试为可选测试，输出数字静音；需要时设置 `TSCUTTER_ENABLE_AUDIO_DEVICE_TESTS=1`，
并确保机器有可用的默认音频设备。未启用时会明确跳过。

应用构建时会编译一个小型原生 PCM 输出桥，编译工具要求及无声构建选项见[音频桥说明](../TSCutter.GUI/native/audio/README_CN.md)。
FFmpeg 解码和重采样仍复用上文所述的现有运行库。

## 可选的自定义目录和真实素材

默认的合成测试不依赖下面的环境变量。它们用于复用已有目录或运行无法通用生成的真实素材检查：

| 变量 | 用途 |
| --- | --- |
| `TSCUTTER_TEST_MEDIA_ROOT` | 可选的合成素材根目录，内含 `keyframes/`、`remux/`、`subtitles/`；与准备脚本的 `--output-dir` 配合使用 |
| `TSCUTTER_REMUX_TEST_SAMPLES` | 兼容旧的封装导出素材目录，优先于公共根目录 |
| `TSCUTTER_SUBTITLE_TEST_SAMPLES` | 兼容旧的字幕素材目录，优先于公共根目录 |
| `TSCUTTER_FFMPEG_TEST_SAMPLES` | 可选的真实编码样本目录：`1080i.ts`、`4K.ts`、`AVS+.ts`、`AVS+_2.ts`、`AVS+_P.ts`、`AVS2.ts`、`AVS3.ts`；检查特殊解码器的位置元数据等行为 |
| `TSCUTTER_DOVI_P81_TEST_SAMPLE` | 可选的单层 Dolby Vision Profile 8.1（兼容 HDR10）MP4，用于关键帧导出检查（MP4 保留 `hvc1`）和帧精确检查；帧精确检查在 2–12 秒之间剪切，该范围内至少应有两个 IDR 帧 |
| `TSCUTTER_DOVI_TEST_SAMPLE` | 可选的 Dolby Vision Profile 5 MP4，视频轨道 0、音频轨道 1，用于导出和 HDR 预览检查；帧精确检查在 2–12 秒之间剪切，该范围内至少应有两个 IDR 帧 |
| `TSCUTTER_AV3A_TEST_SAMPLE` | 可选的 Audio Vivid MP4，用于 AV3A 导出检查 |
| `TSCUTTER_SUBTITLE_REAL_SAMPLE` | 可选的 MP4，恰有一个字幕轨道且 2.5 秒处有活动字幕 |
| `TSCUTTER_DOLBY_E_SAMPLES` | 可选目录，放每个都含一条杜比 E（SMPTE 302M 承载）轨的 TS，用于识别、按声道组解码和 TS 重组检查（重组检查截取每个文件开头 24 MB 并注入传输错误） |

真实素材不下载、不生成、不入库。未配置时只跳过对应的真实素材用例，不影响合成测试和普通测试。
关键帧测试使用公共的 `keyframes` 目录，不需要单独的素材目录变量。
`TSCUTTER_KEYFRAME_STRIP_CAPTURE`、`TSCUTTER_SUBTITLE_PREVIEW_IMAGE`、`TSCUTTER_FRAME_ACCURATE_CAPTURE` 只是可选的截图输出位置，不是测试运行前提。
帧精确剪辑的真实素材检查使用 `TSCUTTER_FFMPEG_TEST_SAMPLES` 中的 `4K.ts`（HEVC）、`1080i.ts`（隔行 H.264）和 `MPEG2.ts`（隔行开放 GOP MPEG-2），杜比视界检查使用 `TSCUTTER_DOVI_TEST_SAMPLE`（Profile 5）和 `TSCUTTER_DOVI_P81_TEST_SAMPLE`（Profile 8.1），分别按 MP4、MKV 剪切，并转为 PMT 带杜比描述符的 TS 后按 TS 剪切。将 `TSCUTTER_FRAME_ACCURATE_KEEP_DIR` 设为已存在的目录时，会保留杜比视界输出（`dovi50-cut.mp4`、`dovi81-cut.mkv` 等），便于用外部工具检查。
