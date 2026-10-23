#!/usr/bin/env python3
"""从程序 SVG 生成经过抗锯齿缩小的 PNG、ICO 与 macOS ICNS。依赖 CairoSVG、Pillow。"""

import argparse
import io
from pathlib import Path
import shutil
import struct
import xml.etree.ElementTree as ET

import cairosvg
from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
SIZES = (16, 24, 32, 48, 64, 128, 256, 512, 1024)
ICO_SIZES = SIZES[:7]


def generate(source: Path, output: Path) -> None:
    output.mkdir(parents=True, exist_ok=True)
    svg = ET.fromstring(source.read_bytes())
    # 像素风格由路径中的台阶保留。小尺寸禁用硬对齐，避免不足一像素的描边消失。
    svg.set("shape-rendering", "geometricPrecision")
    rendered = cairosvg.svg2png(
        bytestring=ET.tostring(svg), output_width=2048, output_height=2048)
    # 预乘透明度后统一缩小，避免透明像素的 RGB 污染细线边缘。
    master = Image.open(io.BytesIO(rendered)).convert("RGBa")
    frames = {}
    for size in SIZES:
        frame = master.resize((size, size), Image.Resampling.LANCZOS).convert("RGBA")
        frame.save(output / f"logo-{size}.png")
        frames[size] = frame
    frames[1024].save(output / "logo.png")
    frames[256].save(
        output / "logo.ico", format="ICO",
        sizes=[(size, size) for size in ICO_SIZES],
        append_images=[frames[size] for size in ICO_SIZES[:-1]])
    svg_output = output / "logo.svg"
    if source.resolve() != svg_output.resolve():
        shutil.copy2(source, svg_output)

    iconset = output / "TSCutter.iconset"
    iconset.mkdir(exist_ok=True)
    for size in (16, 32, 128, 256, 512):
        frames[size].save(iconset / f"icon_{size}x{size}.png")
        frames[size * 2].save(iconset / f"icon_{size}x{size}@2x.png")
    # 使用现代 PNG 表示保存各档 RGBA，避免旧式 RGB + mask 格式转换半透明边缘。
    # 项目支持的 macOS 13+ 可直接读取这些 ICNS 类型。
    entries = ((b"icp4", 16), (b"ic11", 32), (b"icp5", 32), (b"ic12", 64),
               (b"ic07", 128), (b"ic13", 256), (b"ic08", 256), (b"ic14", 512),
               (b"ic09", 512), (b"ic10", 1024))
    chunks = []
    for kind, size in entries:
        png = (output / f"logo-{size}.png").read_bytes()
        chunks.append(kind + struct.pack(">I", len(png) + 8) + png)
    content = b"".join(chunks)
    (output / "logo.icns").write_bytes(b"icns" + struct.pack(">I", len(content) + 8) + content)
    print(f"图标已生成：{output}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=ROOT / "design/icons/app/logo.svg")
    parser.add_argument("--output-dir", type=Path, required=True)
    args = parser.parse_args()
    generate(args.source.resolve(), args.output_dir.resolve())
