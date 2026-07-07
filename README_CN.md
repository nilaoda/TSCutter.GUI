<div align="center">
  <img src="src/TSCutter.GUI/Assets/logo.png" alt="TSCutter.GUI" width="112">
  <h1>TSCutter.GUI</h1>
  <p><a href="README.md">English</a> | <strong>中文版</strong></p>
</div>

TSCutter.GUI 是一个轻量级工具，旨在高效剪切 TS（传输流）视频文件。它允许用户在不修改原始数据的情况下快速浏览关键帧并提取特定片段。

> 该软件仍在开发中，尚未正式发布，因此可能包含 **许多BUG**。

## 功能
- **关键帧预览**: 跳转到下一个、上一个或任何特定的关键帧。
- **关键帧级精确剪切**: 基于关键帧提取视频片段，确保无数据丢失或修改。
- **多平台支持**: 同时支持 Windows、Linux 和 macOS。
- **高性能**: 原理是直接复制二进制数据，理论上和直接复制文件速度一致。

## FFmpeg 运行时
官方发布包已内置 **FFmpeg 7.1.3** 共享库，普通用户无需手动安装。

内置运行时来源：[nilaoda/FFmpegSharedLibraries](https://github.com/nilaoda/FFmpegSharedLibraries/releases/latest)。

> **macOS**：若因隔离属性（quarantine）被拦截，请执行 `xattr -dr com.apple.quarantine TSCutter.GUI.app`。

<details>
<summary>从源码构建</summary>

从源码构建且未内置运行时库时，需自行准备兼容的 FFmpeg 7 环境。

- **macOS**：`brew install ffmpeg@7`
- **Linux (Ubuntu 22.04)**：`sudo add-apt-repository ppa:ubuntuhandbook1/ffmpeg7 && sudo apt update && sudo apt install ffmpeg`

macOS 下程序会自动探测常见的 Homebrew 路径；若 FFmpeg 7 安装在其他位置，可在 `~/Library/Application Support/TSCutter.GUI/config.json` 中设置 `FFmpegRootPath` 为 FFmpeg 根目录或其 `lib` 目录。

</details>

## 界面预览

<img alt="TSCutter.GUI 界面预览" src="img/SS1.png">

## 使用方法

1. 启动应用程序。
2. 加载 TS 文件，或直接拖入文件。
3. 使用关键帧导航按钮找到您想要的起点和终点。
4. 点击“保存”以提取选定的片段。

## 致谢
此项目灵感来自一个出色的DVB视频剪辑软件 [VidePub](https://sourceforge.net/projects/videpub/)。

## 许可证
本项目采用 GPL-3.0 许可证。
