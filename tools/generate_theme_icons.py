#!/usr/bin/env python3
"""将入库的 SVG 源稿转换为 Avalonia 矢量资源；只使用 Python 标准库。"""

import argparse
from pathlib import Path
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
SVG = "{http://www.w3.org/2000/svg}"
NS = "https://github.com/avaloniaui"
X = "http://schemas.microsoft.com/winfx/2006/xaml"
ICONS = ("open", "zoom-in", "zoom-out", "original", "fit", "keyframes",
         "thumbnails", "snapshot", "tools", "info", "save")
PALETTES = {
    "Xp": ("xp", None),
    "Aero": ("aero", None),
    "Windows8": ("flat", "#2672EC"),
    "Windows8Dark": ("flat", "#82BAFF"),
    "Windows10": ("flat", "#0078D7"),
    "Windows10Dark": ("flat", "#60CDFF"),
    "Windows11": ("fluent", "#1A1A1A"),
    "Windows11Dark": ("fluent", "#FFFFFF"),
    "MacOS": ("macos", "#484A50"),
    "MacOSDark": ("macos", "#ECECEF"),
}


def element(parent, name, **attrs):
    return ET.SubElement(parent, name, attrs)


def brush(parent, property_name, value, gradients, color, opacity):
    if value == "none":
        return
    target = element(parent, property_name)
    if value.startswith("url(#"):
        gradient = gradients[value[5:-1]]
        result = element(target, "LinearGradientBrush",
                         StartPoint=f'{gradient.get("x1", "0%")},{gradient.get("y1", "0%")}',
                         EndPoint=f'{gradient.get("x2", "100%")},{gradient.get("y2", "0%")}',
                         Opacity=opacity)
        for stop in gradient:
            if stop.tag != SVG + "stop":
                raise ValueError("不支持的渐变节点")
            offset = stop.get("offset", "0")
            if offset.endswith("%"):
                offset = str(float(offset[:-1]) / 100)
            element(result, "GradientStop", Offset=offset, Color=stop.attrib["stop-color"])
    else:
        if value == "currentColor":
            if color is None:
                raise ValueError("currentColor 缺少配色")
            value = color
        element(target, "SolidColorBrush", Color=value, Opacity=opacity)


def convert(source, color):
    svg = ET.parse(source).getroot()
    # 明确拒绝未实现的绘制特性，避免源稿更新后生成的图形悄悄丢失效果。
    allowed = {
        "svg": {"width", "height", "viewBox", "fill"},
        "defs": set(),
        "linearGradient": {"id", "x1", "y1", "x2", "y2"},
        "stop": {"offset", "stop-color"},
        "path": {"d", "fill", "fill-rule", "stroke", "stroke-width",
                 "stroke-linejoin", "stroke-linecap", "opacity"},
        "circle": {"cx", "cy", "r", "fill", "stroke", "stroke-width", "opacity"},
    }
    for node in svg.iter():
        tag = node.tag.removeprefix(SVG)
        if tag not in allowed or set(node.attrib) - allowed[tag]:
            raise ValueError(f"不支持的 SVG 节点或属性 {tag} {node.attrib}：{source}")
    if svg.get("viewBox") != "0 0 32 32":
        raise ValueError(f"画布必须为 32 × 32：{source}")
    gradients = {node.attrib["id"]: node for node in svg.iter(SVG + "linearGradient")}
    image = ET.Element("DrawingImage", {"x:Key": "Icon." + source.stem})
    group = element(element(image, "DrawingImage.Drawing"), "DrawingGroup")
    # 透明画布固定所有图标的布局边界，切换图形时不产生大小或位置跳变。
    element(group, "GeometryDrawing", Brush="Transparent", Geometry="M0,0 H32 V32 H0 Z")
    for node in svg:
        tag = node.tag.removeprefix(SVG)
        if tag == "defs":
            continue
        if tag == "path":
            geometry = node.attrib["d"]
        elif tag == "circle":
            x, y, r = (float(node.attrib[key]) for key in ("cx", "cy", "r"))
            geometry = f"M{x-r},{y} a{r},{r} 0 1 0 {2*r},0 a{r},{r} 0 1 0 {-2*r},0 Z"
        else:
            raise ValueError(f"不支持的 SVG 节点 {tag}：{source}")
        drawing = element(group, "GeometryDrawing",
                          Geometry=("F0 " if node.get("fill-rule") == "evenodd" else "F1 ") + geometry)
        opacity = node.get("opacity", "1")
        brush(drawing, "GeometryDrawing.Brush", node.get("fill", svg.get("fill", "#000000")),
              gradients, color, opacity)
        if node.get("stroke", "none") != "none":
            pen = element(element(drawing, "GeometryDrawing.Pen"), "Pen",
                          Thickness=node.get("stroke-width", "1"),
                          LineCap=node.get("stroke-linecap", "butt").replace("butt", "Flat").title(),
                          LineJoin=node.get("stroke-linejoin", "miter").title())
            brush(pen, "Pen.Brush", node.attrib["stroke"], gradients, color, opacity)
    return image


def generate(check):
    mismatched = []
    for palette, (family, color) in PALETTES.items():
        dictionary = ET.Element("ResourceDictionary", {"xmlns": NS, "xmlns:x": X})
        for name in ICONS:
            dictionary.append(convert(ROOT / "design/icons" / family / f"{name}.svg", color))
        ET.indent(dictionary, space="    ")
        result = ("<!-- 此文件由 tools/generate_theme_icons.py 生成，请修改 design/icons 中的源稿。 -->\n"
                  + ET.tostring(dictionary, encoding="unicode") + "\n").encode()
        target = ROOT / "src/TSCutter.GUI/Themes/Icons" / f"{palette}.axaml"
        if check:
            if not target.exists() or target.read_bytes() != result:
                mismatched.append(str(target.relative_to(ROOT)))
        else:
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(result)
    if mismatched:
        raise SystemExit("矢量资源需要重新生成：\n" + "\n".join(mismatched))
    print("矢量图标资源校验通过。" if check else f"已生成 {len(PALETTES)} 套矢量图标资源。")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="检查已生成资源是否与源稿一致")
    generate(parser.parse_args().check)
