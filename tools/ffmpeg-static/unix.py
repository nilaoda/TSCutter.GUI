"""Translate the SDK's pkg-config link order into NativeAOT linker arguments."""
import os
from pathlib import Path
import shlex
import subprocess
import xml.etree.ElementTree as ET


def compiler_archive(name):
    compiler = "g++" if name == "stdc++" else "gcc"
    filename = f"lib{name}.a"
    path = Path(subprocess.check_output([compiler, f"-print-file-name={filename}"], text=True).strip())
    if not path.is_absolute() or not path.is_file():
        raise FileNotFoundError(f"The compiler did not provide {filename}: {path}")
    return path.resolve()


def resolve_flags(flags, sdk, platform, resolve_runtime=None):
    resolve_runtime = resolve_runtime or compiler_archive
    result, archives = [], []
    system = {"m", "c", "dl", "pthread", "rt", "util"}
    if platform == "macos":
        system |= {"c++", "objc", "System", "iconv", "icucore"}
    index = 0
    while index < len(flags):
        flag = flags[index]
        index += 1
        archive = None
        if flag == "-framework":
            if platform != "macos" or index == len(flags) or flags[index].startswith("-"):
                raise ValueError("Invalid framework argument in FFmpeg link metadata")
            result.extend((flag, flags[index]))
            index += 1
            continue
        if flag.startswith("-L"):
            if flag == "-L":
                index += 1
            continue  # Bundled libraries are resolved to exact archives below.
        if flag.startswith("-l"):
            name = flag[2:]
            candidate = sdk / "lib" / f"lib{name}.a"
            if candidate.is_file():
                archive = candidate.resolve()
            elif platform == "linux" and name in {"stdc++", "gcc", "gcc_s", "atomic"}:
                archive = resolve_runtime("gcc_eh" if name == "gcc_s" else name)
            elif name not in system:
                raise FileNotFoundError(f"Static SDK does not contain lib{name}.a")
        elif flag.endswith(".a"):
            archive = (sdk / "lib" / Path(flag).name).resolve()
            if not archive.is_file():
                raise FileNotFoundError(f"Cannot relocate SDK archive: {flag}")
        elif flag in {"-static-libgcc", "-static-libstdc++"}:
            continue  # Compiler runtimes are resolved to explicit archives on Linux.
        elif flag != "-pthread":
            raise ValueError(f"Unsupported FFmpeg SDK linker option: {flag}")
        if archive is not None:
            result.append(str(archive))
            archives.append(archive)
        else:
            result.append(flag)
    if platform == "linux":
        # Rescan codec/font archives and their C++ support for circular references.
        result = ["-Wl,--start-group", *result, "-Wl,--end-group"]
    return result, list(dict.fromkeys(archives))


def prepare(sdk, output, platform):
    env = os.environ.copy()
    env["PKG_CONFIG_PATH"] = ""
    env["PKG_CONFIG_LIBDIR"] = str(sdk / "lib/pkgconfig")
    env.pop("PKG_CONFIG_SYSROOT_DIR", None)
    flags = shlex.split(subprocess.check_output(
        ["pkg-config", "--static", "--libs", "ffmpeg-static"], env=env, text=True))
    arguments, archives = resolve_flags(flags, sdk, platform)
    response = output / "ffmpeg-link.rsp"
    # Clang response-file quoting keeps SDK paths containing spaces intact.
    response.write_text("\n".join('"' + argument.replace("\\", "\\\\").replace('"', '\\"') + '"'
                                  for argument in arguments) + "\n", encoding="utf-8")
    project = ET.Element("Project")
    properties = ET.SubElement(project, "PropertyGroup")
    ET.SubElement(properties, "LinkStandardCPlusPlusLibrary").text = "true"
    ET.SubElement(properties, "FFmpegStaticResponseFile").text = str(response)
    items = ET.SubElement(project, "ItemGroup")
    for archive in archives:
        ET.SubElement(items, "FFmpegStaticArchive", Include=str(archive))
    ET.SubElement(items, "NativeLibrary", Include="@(FFmpegStaticArchive);$(FFmpegStaticResponseFile)")
    ET.indent(project)
    ET.ElementTree(project).write(output / "NativeLibraries.props", encoding="utf-8", xml_declaration=True)
    print(f"Prepared {len(archives)} static archives and {platform} linker metadata: {response}")
