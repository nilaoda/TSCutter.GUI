#!/usr/bin/env python3
"""Validate the native graphics SDK and emit NativeAOT link inputs."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET

LIBRARIES = {
    "win-x64": ("libHarfBuzzSharp.lib", "skia.lib", "SkiaSharp.lib", "libANGLE_static.lib", "libGLESv2_static.lib"),
    "win-arm64": ("libHarfBuzzSharp.lib", "skia.lib", "SkiaSharp.lib", "libANGLE_static.lib", "libGLESv2_static.lib"),
    "linux-x64": ("libHarfBuzzSharp.a", "libSkiaSharp.a", "libskia.a"),
    "linux-arm64": ("libHarfBuzzSharp.a", "libSkiaSharp.a", "libskia.a"),
    "osx-arm64": ("libHarfBuzzSharp.a", "libSkiaSharp.a", "libskia.a", "libAvaloniaNative.a"),
    "osx-x64": ("libHarfBuzzSharp.a", "libSkiaSharp.a", "libskia.a", "libAvaloniaNative.a"),
}


def prepare(sdk, output, rid, ffmpeg=None):
    sdk, output = sdk.resolve(), output.resolve()
    manifest = json.loads((sdk / "manifest.json").read_text(encoding="utf-8"))
    if manifest["rid"] != rid or rid not in LIBRARIES:
        raise ValueError("The native graphics SDK architecture does not match the target RID")
    components = manifest["components"]
    if components["skia"]["version"] != "2.88.9" or components["harfbuzz"]["binding_version"] != "8.3.1.1":
        raise ValueError("The native graphics SDK must match SkiaSharp 2.88.9 and HarfBuzzSharp 8.3.1.1")
    if rid.startswith("osx-") and components["avalonia"]["version"] != "11.3.23":
        raise ValueError("The native windowing SDK must match Avalonia.Native 11.3.23")
    if rid.startswith("win-") and components["angle"]["version"] != "chromium/7151":
        raise ValueError("The native graphics SDK must contain ANGLE chromium/7151")
    if rid.startswith("osx-"):
        for name in ("build/StaticSkia.targets", "build/StaticSkiaLoader.c"):
            path = sdk / name
            if not path.is_file():
                raise ValueError(f"The macOS static Metal support file is missing: {name}")
            with path.open("rb") as support:
                digest = hashlib.file_digest(support, "sha256").hexdigest()
            if digest != manifest.get("support_sha256", {}).get(name):
                raise ValueError(f"Static Metal support checksum mismatch: {name}")
    paths = []
    for name in LIBRARIES[rid]:
        path = sdk / "lib" / name
        with path.open("rb") as library:
            digest = hashlib.file_digest(library, "sha256").hexdigest()
        if digest != manifest["sha256"].get(name):
            raise ValueError(f"Native graphics SDK checksum mismatch: {name}")
        paths.append(path)
    output.mkdir(parents=True, exist_ok=True)
    project = ET.Element("Project")
    items = ET.SubElement(project, "ItemGroup")
    for path in paths:
        ET.SubElement(items, "NativeLibrary", Include=str(path))
    libs = list(dict.fromkeys(name for entry in manifest["link"].values() for name in entry["libs"]))
    flags = []
    for name in libs:
        if rid.startswith("win-"):
            if not re.fullmatch(r"[A-Za-z0-9_]+[.]lib", name):
                raise ValueError(f"Unexpected Windows system library: {name}")
            ET.SubElement(items, "NativeLibrary", Include=name)
        elif name in {"m", "pthread", "dl", "c++", "objc"}:
            flags.append("-l" + name)
        elif ffmpeg and (ffmpeg / "lib" / f"lib{name}.a").is_file():
            # FFmpeg's response file already links this bundled dependency statically.
            continue
        else:
            raise ValueError(f"A bundled static dependency is required for graphics library {name}")
    if not rid.startswith("win-"):
        frameworks = dict.fromkeys(name.removesuffix(".framework") for entry in manifest["link"].values()
                                   for name in entry["frameworks"])
        for name in frameworks:
            if not re.fullmatch(r"[A-Za-z0-9_]+", name):
                raise ValueError(f"Unexpected system framework: {name}")
            flags.extend(("-framework", name))
        if rid.startswith("osx-"):
            flags.append("-Wl,-ObjC")
        response = output / "graphics-link.rsp"
        response.write_text("\n".join(json.dumps(flag) for flag in flags) + "\n", encoding="utf-8")
        properties = ET.SubElement(project, "PropertyGroup")
        ET.SubElement(properties, "NativeGraphicsResponseFile").text = str(response)
        ET.SubElement(items, "NativeLibrary", Include="$(NativeGraphicsResponseFile)")
    ET.indent(project)
    ET.ElementTree(project).write(output / "NativeLibraries.props", encoding="utf-8", xml_declaration=True)
    print(f"Prepared {rid} static graphics: {len(paths)} archives")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sdk", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--rid", choices=LIBRARIES, required=True)
    parser.add_argument("--ffmpeg-sdk", type=Path)
    args = parser.parse_args()
    prepare(args.sdk, args.output, args.rid, args.ffmpeg_sdk.resolve() if args.ffmpeg_sdk else None)


if __name__ == "__main__":
    main()
