# macOS 主题图标

11 个独立 SVG，使用 32 × 32 透明画布与 `currentColor`。以圆角轮廓、统一视觉重量和适当留白配合 macOS 材质。主要描边宽度为 1.8，细节较多的胶片采用 1.6，关键帧菱形使用实心以保证小尺寸辨识度。

图标名称沿用现有主题资源键。`save` 用于片段导出，以向外箭头表达输出文件；关键帧使用胶片与菱形，缩略图使用圆角画面网格。

运行 `python3 tools/generate_theme_icons.py` 生成 `MacOS.axaml` 与 `MacOSDark.axaml`，分别用于 macOS 浅色及深色主题。运行 `python3 tools/generate_theme_icons.py --check` 检查资源与源稿的一致性。
