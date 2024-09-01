# TSCutter.GUI
[English](./README.md) | 中文版

TSCutter.GUI 是一个轻量级工具，旨在高效剪切 TS（传输流）视频文件。它允许用户在不修改原始数据的情况下快速浏览关键帧并提取特定片段。

> 该软件仍在开发中，尚未正式发布，因此可能包含 **许多BUG**。

## 功能
- **关键帧预览**: 跳转到下一个、上一个或任何特定的关键帧。
- **关键帧级精确剪切**: 基于关键帧提取视频片段，确保无数据丢失或修改。
- **多平台支持**: 同时支持 Windows、Linux 和 macOS。
- **高性能**: 原理是直接复制二进制数据，理论上和直接复制文件速度一致。

## 前置准备
此软件依赖于 **FFmpeg 7.0** 的动态库。
### Windows
直接运行 `.exe` 即可。 内置库：[Sdcb.FFmpeg.runtime.windows-x64](https://www.nuget.org/packages/Sdcb.FFmpeg.runtime.windows-x64/7.0.0)
### Linux
在 Linux 上安装 FFmpeg 取决于您使用的发行版。

在 Ubuntu 22.04 上：
```bash
sudo add-apt-repository ppa:ubuntuhandbook1/ffmpeg7
sudo apt update
sudo apt install ffmpeg
```
### macOS
```bash
brew install ffmpeg
```

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
本项目采用 LGPL-3.0 许可证。