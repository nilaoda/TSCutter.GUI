# FFmpeg 9 迁移记录

分支：`feature/ffmpeg9-runtime`。运行时固定使用 FFmpegSharedLibraries 的 `20261004` release（FFmpeg 9.0.2），应用版本及发布策略不变。

## 实现

- 移除 Sdcb.FFmpeg 包及对应 rd.xml 裁剪声明；引用 FFmpeg.AutoGen.Abstractions 9.0.1.1 的原生结构/枚举。项目内只声明实际使用的 LibraryImport 接口，支持 NativeAOT，字符串使用 UTF-8。
- 三平台统一按 ABI 加载、验证原生库：avutil 61、avcodec/avformat/avdevice 63、avfilter 12、swscale 10、swresample 7。支持应用目录、配置目录、环境变量及兼容的系统目录；不再创建 macOS 运行时软链接。
- 使用 `AV_CODEC_FLAG_COPY_OPAQUE` 传递 `pos + 1` 数值令牌，保留零作为“来源未知”。无逐包 GCHandle、AVBufferRef 或压缩数据缓存。AVS2/AVS3 则启用补丁运行时的 `export_packet_pos` 元数据。
- 不使用当前输入包的位置替代延迟输出帧的位置；来源未知时禁止添加片段及标记边界，仍可预览和操作已有片段。
- AVS2/AVS3 缺少私有位置选项时继续打开解码器用于预览，帧位置固定为未知；支持扩展时也只使用解码器元数据，不回退到 opaque。其他选项错误仍正常报告。
- 设置解码器 pkt_timebase；CAVS 会修正重复/倒退的 PTS，因此其来源验证同时检查位置令牌与解码器导出的包位置，不仅依赖输出 PTS。
- 解码帧复用；结构体枚举器避免逐包托管分配。提前结束预览时取完并释放剩余输出，连续画面搜索在下一次读取前先接收原生解码器的剩余输出，文件尾发送空包取出全部延迟关键帧；seek 后清空解码器。输入上下文复用一个原生包，载荷引用由读取枚举器统一释放。
- swscale 直接读取原生平面/步长，省掉原实现每次转换的四个托管数组。保留双位图租约、硬件帧回读及软解回退。
- Help/About 更新运行时仓库、绑定和参考代码来源。原始参考代码保留在源码仓库；发布产物只附带一个包含声明与许可原文的 `THIRD_PARTY_NOTICES.txt`。

## 验证（2026-10-04）

- 完整测试：353 个通过（包含启用真实运行时的集成测试）。新增回归覆盖缺少私有选项时的 AVS2/AVS3 解码，以及拒绝不可靠 opaque/元数据位置。
- 真实素材：1080i.ts、4K.ts、AVS+.ts、AVS+_2.ts、AVS+_P.ts、AVS2.ts、AVS3.ts，核对来源包位置、关键包标记、重排、seek 后输出和 drain。共验证 786 帧；部分素材本身有损坏/缺少参考帧，验证覆盖成功输出的画面。
- 原生中断回调：强制 GC 后仍有效；取消可中断原生输入。
- 稳定尺寸连续 100 次 swscale 转换，当前线程新增托管分配为 0。
- macOS ARM64：完整 GUI NativeAOT publish 成功；使用同一绑定源码的独立 NativeAOT 程序验证 H.264、AVS2、8K AVS3 解码与转换。AVS3 首帧程序峰值 RSS 约 626 MiB，此为单次首帧测量，不代表长期运行上限。
- GUI 实际解码链路：H.264、HEVC、AVS2、8K AVS3 各连续定位/预览 8 次，并读取媒体信息。HEVC 使用 VideoToolbox + CPU 回读；1080i 的硬件初始化失败后正常使用软解。
- Windows ARM64 虚拟机中的 x64 运行：使用 release 的 Windows 动态库与独立自包含 .NET 程序，通过 H.264、AVS2 解码和转换。此处验证的是 JIT 程序，不是 Windows NativeAOT GUI。
- Linux：已核对 release 包文件名及 ABI；尚未运行 Linux GUI。Windows GUI NativeAOT 和 Windows 硬解仍需对应平台验证。

## 第三方声明整理（2026-10-04）

- 主清单仅列直接使用的主要项目，FFmpegSharedLibraries 不展开内部依赖；Inter 字体保留 SIL OFL 1.1。
- 区分 NuGet 图形库与 Windows 静态构建来源，保留各自原始许可；.NET 声明在发布时取自实际运行时包。
- Help 添加声明文件定位入口；项目链接只显示名称，悬停显示 URL。许可文件不在启动或打开 Help 时加载。
- 构建时通过 MSBuild 流式合并声明、GPL v3 及第三方许可原文为 `THIRD_PARTY_NOTICES.txt`；普通构建和发布都只复制这个文件。原文继续分别保留在源码仓库的 `ThirdParty` 目录，参考源码不合并到声明。
- macOS ARM64 NativeAOT 发布成功，声明与全部许可输入通过原文完整性校验；发布目录只含一个声明文件，无 `ThirdParty` 目录和单独的 Markdown。Windows ZIP 排除规则也未过滤该声明。Windows/Linux 完整发布仍由对应平台流水线验证。

## 复现测试

常规测试不依赖本地素材，原生集成测试会明确标为跳过。设置以下环境变量即可启用：

```sh
TSCUTTER_FFMPEG_ROOT=/path/to/ffmpeg9-runtime \
TSCUTTER_FFMPEG_TEST_SAMPLES=/path/to/Samples \
dotnet test src/TSCutter.GUI.sln -c Debug
```

Samples 目录需要包含上述七个素材及 1080i.ts；运行时需使用上述 release 中对应平台的动态库。AVS2/AVS3 来源扩展不属于普通系统 FFmpeg 的标准接口。
