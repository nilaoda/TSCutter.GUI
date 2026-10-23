# 程序图标

[logo.svg](logo.svg) 是程序图标的矢量源稿，采用 256 × 256 透明画布。胶片、齿孔、手柄、刀刃和转轴使用独立路径，手柄孔透明，像素台阶和浅色描边保存在路径中。

安装 CairoSVG 和 Pillow 后，在仓库根目录生成各平台资源：

```sh
python3 tools/generate_app_icons.py --output-dir /tmp/tscutter-icons
```

将生成的 `logo.png`、`logo.ico` 和 `logo.icns` 复制到 `src/TSCutter.GUI/Assets/`：

| 文件 | 用途 |
| --- | --- |
| `logo.png` | 1024 × 1024 透明图片，用于应用内展示 |
| `logo.ico` | Windows 可执行文件及窗口图标，包含 16、24、32、48、64、128、256 像素各档 |
| `logo.icns` | macOS 应用图标，包含普通和 Retina 表示，最高 1024 × 1024 |

生成流程先渲染 2048 像素中间图，再以预乘透明度和 Lanczos 采样缩小，保留小尺寸细线的抗锯齿覆盖。ICNS 各档采用 PNG 表示，另外输出十档标准 iconset。

README 直接引用 SVG，应用使用导出资源，macOS 打包直接复制 ICNS。更新后同时检查大图和 Dock 尺寸的描边、齿孔与手柄孔。
