# TSCutter.GUI
[English](./README.md) | 中文版

TSCutter.GUI 是一个轻量级工具，旨在高效剪切 TS（传输流）视频文件。它允许用户在不修改原始数据的情况下快速浏览关键帧并提取特定片段。

> 该软件仍在开发中，尚未正式发布，因此可能包含 **许多BUG**。

## 功能
- **关键帧预览**: 跳转到下一个、上一个或任何特定的关键帧。
- **关键帧级精确剪切**: 基于关键帧提取视频片段，确保无数据丢失或修改。
- **多平台支持**: 同时支持 Windows、Linux 和 macOS。
- **高性能**: 原理是直接复制二进制数据，理论上和直接复制文件速度一致。

## FFmpeg 运行时
官方发布包已经内置 **FFmpeg 7.1.3** 所需的共享库，普通用户现在不需要再手动安装 FFmpeg。

内置运行时来源： [nilaoda/FFmpegSharedLibraries](https://github.com/nilaoda/FFmpegSharedLibraries/releases/tag/20260306)。TSCutter.GUI 发布包所使用的 Windows x64、Linux x64 和 macOS arm64 的 FFmpeg 共享库都统一由这个仓库维护。

macOS arm64 的发布压缩包现在解压后就是 `TSCutter.GUI.app`，用户解压后即可直接运行。

如果 macOS 因隔离属性（quarantine）阻止应用启动，请执行：
```bash
xattr -dr com.apple.quarantine TSCutter.GUI.app
```

如果你是从源码运行，并且当前构建产物里没有内置这些运行时库，那么仍然需要准备兼容的 FFmpeg 7 环境。

### Linux 源码运行
在 Linux 上安装 FFmpeg 取决于您使用的发行版。

在 Ubuntu 22.04 上：
```bash
sudo add-apt-repository ppa:ubuntuhandbook1/ffmpeg7
sudo apt update
sudo apt install ffmpeg
```

### macOS 源码运行
```bash
brew install ffmpeg@7
```

程序会优先查找发布包中内置的 dylib；如果不存在，再自动探测常见的 Homebrew 路径，例如 `/opt/homebrew/opt/ffmpeg@7/lib` 和 `/usr/local/opt/ffmpeg@7/lib`。

如果你的 FFmpeg 7 安装在其他位置，可以在 `~/Library/Application Support/TSCutter.GUI/config.json` 中设置 `FFmpegRootPath`，填写 FFmpeg 根目录或其 `lib` 目录。

## 截图
![img](img/SS1.png)

## 使用方法

1. 启动应用程序。
2. 加载 TS 文件，或直接拖入文件。
3. 使用关键帧导航按钮找到您想要的起点和终点。
4. 点击“保存”以提取选定的片段。

## 致谢
此项目灵感来自一个出色的DVB视频剪辑软件 [VidePub](https://sourceforge.net/projects/videpub/)。

## 许可证
本项目采用 GPL-3.0 许可证。
