# 源码结构

[English](README.md) | 简体中文

`TSCutter.GUI` 是桌面应用，`TSCutter.GUI.Generators` 提供本地化源码生成器，`TSCutter.GUI.Tests` 保存测试。

## 应用分层

| 目录 | 职责 |
| --- | --- |
| `Models` | 配置、工程、媒体状态及各工具的数据模型 |
| `Services` | 文件扫描、导出、修复等业务流程，以及配置与本地化服务 |
| `ViewModels` | 窗口状态、命令及交互协调 |
| `Views` | 窗口 XAML 及事件适配代码 |
| `Utils` | 文件、媒体、时间戳、报告等可复用辅助逻辑 |
| `FFmpeg` | 原生接口声明与编解码对象封装 |
| `Rendering` | 视频帧呈现与图集合成 |
| `Controls`、`Converters` | 通用控件与绑定转换器 |
| `Themes`、`Lang`、`Assets` | 主题、翻译和静态资源 |

## 功能分组

较大的分层目录内部按功能分组：

| 分组 | 内容 |
| --- | --- |
| `Application` | 配置、语言、主题、关于与更新 |
| `Editing` | 主编辑器、片段、工程、导出队列、剪辑与合并 |
| `Preview` | 视频与字幕预览、关键帧总览、图集、截图和媒体信息 |
| `FrameSearch` | 画面搜索、匹配及模板管理 |
| `TransportStream/Analysis` | TS 检查、节目表解析、包查看及报告 |
| `TransportStream/Processing` | TS 二进制合并、ES 提取、流过滤与编辑 |
| `TransportStream/Repair` | TS 时间轴修复、多源修复及修复结果检查 |

`Utils/Files` 集中文件路径、自然排序和拖放处理，`Utils/Media` 集中运行库加载与通用媒体读取，`Utils/TransportStream/Packets` 保存 TS 包和时间戳等底层辅助代码。跨功能使用的集合及基础工具保留在 `Utils` 根目录。

## 目录与命名空间

C# 命名空间与项目内的目录一致。例如：

```text
Services/Editing/RemuxExportService.cs
  → TSCutter.GUI.Services.Editing
ViewModels/TransportStream/Repair/TsTimelineRepairWindowViewModel.cs
  → TSCutter.GUI.ViewModels.TransportStream.Repair
Views/TransportStream/Repair/TsTimelineRepairWindow.axaml.cs
  → TSCutter.GUI.Views.TransportStream.Repair
```

同一窗口的 View 与 ViewModel 使用相同的功能目录。`ViewLocatorBase` 通过将命名空间中的 `.ViewModels.` 替换为 `.Views.` 定位窗口；XAML 的 `x:Class`、类型命名空间及绑定类型也与 C# 声明保持一致。

`LocalizationManager` 位于 `Utils/Application`，本地化生成器输出到 `TSCutter.GUI.Utils.Application`，与手写的 partial 类一致。原生接口仍集中在 `FFmpeg`，新增功能继续复用这一层。

## 测试

测试按对应功能归类；`FFmpeg` 保存原生互操作测试，`UI` 保存主题、控件布局与窗口定位测试。

```sh
dotnet test src/TSCutter.GUI.Tests -c Debug
```

默认可运行普通逻辑和无头 UI 测试。执行 `python tools/prepare-test-media.py` 可统一准备关键帧、封装导出和字幕的合成素材，无需设置素材环境变量；视频生成在已被 Git 忽略的目录，不入库。Windows、macOS、Linux 的完整步骤、运行库查找和可选真实素材说明见 [测试说明](TSCutter.GUI.Tests/README_CN.md)。缺少前提的集成测试会明确显示跳过原因。
