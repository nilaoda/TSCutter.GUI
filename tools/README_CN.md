# 发布与测试工具

[English](README.md) | 简体中文

## 程序图标

SVG 原稿位于 [design/icons/app/logo.svg](../design/icons/app/logo.svg)。安装 CairoSVG 和 Pillow 后，从仓库根目录生成各平台资源：

```sh
python3 tools/generate_app_icons.py --output-dir /tmp/tscutter-icons
```

将 `logo.png`、`logo.ico`、`logo.icns` 复制到 `src/TSCutter.GUI/Assets/`。
生成器先渲染 2048 像素图像，再使用预乘 Alpha 缩小，保留小尺寸下的细描边；同时导出多个尺寸的 PNG 和标准 macOS iconset。
生成后的平台图标会提交到仓库，普通构建和 CI 打包不需要 Python 或 SVG 渲染环境。

## 第三方许可说明

`generate_third_party_notices.py` 将 `THIRD_PARTY_NOTICES.md`、`THIRD_PARTY_LICENSES.txt`、项目的 `LICENSE` 和 .NET 运行时许可合并为一个 UTF-8 文件。
脚本保留上游正文和换行格式，只移除输入文件的 BOM。
`THIRD_PARTY_LICENSES.txt` 对相同许可正文只保留一份，并在各节标题中列出对应组件。
Sdcb 的原始参考代码单独保存在 `design/reference/Sdcb.FFmpeg.VideoFrameConverter.cs.txt`。

应用工程不会导入或执行这个工具。普通 `dotnet build` 和 `dotnet publish` 不依赖仓库中的许可文档或 Python。
共享的 `release-info` 任务生成一次 TXT，并上传为 `third-party-notices` 构建产物；Windows、Linux、macOS 打包任务下载同一份文件。
macOS 包中，该 TXT 与可执行文件一起放在 `TSCutter.GUI.app/Contents/MacOS` 内。

三个平台的构建使用共享任务选定的同一个 SDK 版本。这些目标平台的 .NET 运行时许可正文相同，只有 Windows 的换行格式不同，因此共享任务从该 SDK 解析出的 Linux 运行时包中读取许可。
合并后的依赖许可同时包含 Windows 静态图形库和跨平台共享库。

本地制作独立部署包时，应使用与发布相同的工程、配置和运行时标识符，查询实际解析出的运行时包。
例如，安装 Python 3 和 .NET SDK 后，在仓库根目录用 Bash 执行以下 Linux 打包命令：

```sh
dotnet publish src/TSCutter.GUI -r linux-x64 -c Release -o artifact
dotnet msbuild src/TSCutter.GUI/TSCutter.GUI.csproj -nologo \
  -target:ResolveFrameworkReferences -property:Configuration=Release \
  -property:RuntimeIdentifier=linux-x64 -property:SelfContained=true \
  -getItem:ResolvedRuntimePack > runtime-packs.json
python3 tools/generate_third_party_notices.py \
  --runtime-packs runtime-packs.json --output artifact/THIRD_PARTY_NOTICES.txt
```

生成结果会追加实际运行时包中的 `LICENSE.TXT` 和 `THIRD-PARTY-NOTICES.TXT`。
缺少运行时元数据或许可文件时，打包步骤会失败，不会回退到旧版本的许可正文。

只预览静态许可文档、不包含运行时许可时，可以执行：

```sh
python3 tools/generate_third_party_notices.py --output artifact/THIRD_PARTY_NOTICES.txt
```

该脚本只使用 Python 标准库。运行工具测试：

```sh
python3 -m unittest discover -s tools -p 'test_*.py'
```

## 运行测试

Windows、macOS 和 Linux 使用相同流程。安装 .NET 10 后，可以直接运行普通逻辑和无头 UI 测试，无需设置素材环境变量：

```sh
dotnet test src/TSCutter.GUI.Tests -c Debug
```

需要运行合成媒体集成测试时，先安装 Python 3，以及带 `libx264`、`libx265`、`dvbsub` 编码器的 FFmpeg CLI，然后执行：

```sh
python tools/prepare-test-media.py
dotnet test src/TSCutter.GUI.Tests -c Debug
```

Windows PowerShell 同样可以使用这两条命令；如果 macOS/Linux 上的 Python 命令是 `python3`，替换命令名即可。
准备脚本通过 `--ffmpeg` 和 `--ffprobe` 接受程序路径，支持带空格的路径，不依赖特定 shell。例如 Windows：

```powershell
python tools/prepare-test-media.py --ffmpeg "D:\Media Tools\ffmpeg.exe" --ffprobe "D:\Media Tools\ffprobe.exe"
```

C# 测试还需要应用使用的 FFmpeg 9 共享运行库。它与生成素材时使用的命令行工具是两个独立的前提，只有 `ffmpeg.exe` 不能代替共享库。

统一入口默认准备以下三组素材，也可只生成指定组：

```sh
python tools/prepare-test-media.py --group keyframes
python tools/prepare-test-media.py --group remux subtitles
```

| 组 | 主要内容 |
| --- | --- |
| `keyframes` | 固定和变长 GOP、密集 GOP、B 帧、分数帧率、非零 PTS 的 TS、H.264/HEVC、HLG、短片和单帧 |
| `remux` | MP4/MKV 导出、多音轨、字幕、章节、附件、封面、HDR 和 open GOP |
| `subtitles` | mov_text、ASS 动画、PGS 和 DVB 字幕 |

`--output-dir` 可指定生成目录，`--font` 可为字幕字体附件测试指定字体。
默认目录为 `artifacts/test-media/`，已被 Git 忽略；构建测试项目时，生成素材会自动复制到测试输出目录供测试使用。
视频不下载、不提交到仓库，普通测试运行不会重新生成素材。

`generate-keyframe-test-samples.py`、`generate-remux-export-samples.py`、`generate-subtitle-preview-samples.py` 仍可单独执行。
这些脚本的目录参数现在可省略，默认写入统一目录下对应的组。以前通过环境变量指定的封装导出和字幕目录仍兼容。
测试跳过时，会明确说明缺少的是生成素材、FFmpeg 运行库，还是可选的真实视频。

FFmpeg 运行库的查找方式、Windows 配置命令、环境变量含义，以及 AVS、Dolby Vision、Audio Vivid、字幕真实素材的可选检查，见[测试说明](../src/TSCutter.GUI.Tests/README_CN.md)。
