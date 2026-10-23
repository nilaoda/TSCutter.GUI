# 主题图标

应用 Logo 使用统一资源，不参与主题切换。Classic 沿用 `src/TSCutter.GUI/Assets` 中的原有位图。

| 源稿目录 | 使用主题 | 特征 |
| --- | --- | --- |
| `xp` | Luna 的全部配色，包括深色 | 饱和色彩、明确描边和渐变 |
| `aero` | Windows 7 浅色及深色 | 玻璃镜片、金属细节和柔和高光 |
| `flat` | Windows 8/10 浅色及深色 | 共用形状，通过 `currentColor` 调整颜色 |
| `fluent` | Windows 11 浅色及深色 | 圆角线形图标，按明暗模式切换颜色 |
| `macos` | macOS 浅色及深色 | 单色圆角轮廓、胶片关键帧与导出箭头，按明暗模式切换颜色 |

每套包含打开、放大、缩小、原始大小、适应窗口、关键帧、缩略图、截图、工具、媒体信息和保存，共 11 个图标。源稿为独立的 32 × 32 SVG，无字体或外部图片依赖，可以直接在矢量编辑器中修改。

运行以下命令生成 Avalonia `DrawingImage` 资源：

```sh
python3 tools/generate_theme_icons.py
python3 tools/generate_theme_icons.py --check
```

生成结果位于 `src/TSCutter.GUI/Themes/Icons`，与源稿一起入库。运行时直接绘制矢量，不需要 SVG 解析库，不会放大旧位图，也不缓存不同 DPI 的位图副本。

转换工具仅依赖 Python 标准库，支持当前源稿使用的路径、圆形、线性渐变、填充、描边和透明度。新增其他 SVG 特性时需同时扩展转换工具，避免丢失效果。Windows 8/10 的颜色集中定义在脚本的 `PALETTES` 中。
