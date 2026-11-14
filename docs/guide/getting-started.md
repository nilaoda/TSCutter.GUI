# 快速开始

TSCutter.GUI 是一款跨平台视频剪辑工具，支持快速的关键帧剪切和精确到帧的智能剪辑，同时集成媒体混流、MPEG-TS 分析与修复功能。适用于 **Windows、macOS 和 Linux**。

## 下载

从 [GitHub Releases](https://github.com/nilaoda/TSCutter.GUI/releases/latest) 下载对应平台的最新 Beta 版。

官方发布包已内置所需的 [FFmpeg 运行库](https://github.com/nilaoda/FFmpegSharedLibraries)，无需另行安装。

::: warning 处理器要求
x64 电脑的 CPU 需支持 SSE4.2 和 POPCNT（x86-64-v2）。不支持的有 Intel Core 2、2013 年以前的 Atom，以及 AMD Athlon 64、Phenom、Athlon II、A 系列 3000 和 E 系列（Bobcat）；在这些 CPU 上程序打开后会直接退出，请使用 0.7.1 或更早的版本。苹果芯片和 Arm64 设备不受影响。
:::

::: tip macOS
如果应用因隔离属性被拦截，可在终端执行：

```sh
xattr -dr com.apple.quarantine TSCutter.GUI.app
```
:::

## 四步上手

1. 打开视频，或将文件拖入窗口。
2. 定位画面，标记片段起止点。需要按帧剪切时开启 **智能剪辑**；需要去掉片段时开启 **删除**。
3. 保存单个片段、合并选中片段，或导出删除所选范围后的内容。
4. 保存 `.tscut` 工程，之后继续编辑。

最近打开过的视频、光盘和工程可以从 **文件 → 最近打开** 再次打开，最多保留 10 项，前 9 项可按数字键直接选择。重新打开光盘时会预先选中上次的标题；找不到的文件会标出，点击时询问是否移除。可在 **工具 → 设置 → 常规** 中关闭记录或清除列表。

<Screenshot name="editor" alt="主界面：预览、导航条、时间轴与片段列表" />

## 两种剪辑方式

| 模式 | 处理方式 |
| --- | --- |
| **关键帧剪辑**（默认） | 在兼容的关键帧处剪切，不重新编码音视频。 |
| **智能剪辑** | 按帧选择起止点，只重新编码切点附近的视频，中间部分直接复制；音频和字幕不重新编码。 |

开启 **删除**，即可把广告、片头片尾等标记为要去掉的范围，再将其余内容合并导出。删除模式可与两种剪辑方式搭配使用。

## 支持的格式

- 剪辑 TS/M2TS，重封装导出 MP4/MOV 和 MKV/WebM。
- 预览 FFmpeg 支持的通用视频格式。
- 打开 DVD-Video 与未加密蓝光的 ISO 镜像或光盘文件夹。

## 反馈与社区

- [反馈问题](https://github.com/nilaoda/TSCutter.GUI/issues)
- [Discord 社区](https://discord.gg/qG7kk9GQ44)
