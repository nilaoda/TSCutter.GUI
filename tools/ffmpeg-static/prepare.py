#!/usr/bin/env python3
"""Prepare FFmpeg static libraries for Windows, Linux and macOS NativeAOT."""
import argparse
from pathlib import Path
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
from coff import normalize_archive


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mingw", type=Path, default=Path("C:/msys64/mingw64"))
    parser.add_argument("--sdk", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    mingw, sdk, output = args.mingw.resolve(), args.sdk.resolve(), args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    platform = {"win32": "windows", "linux": "linux", "darwin": "macos"}.get(sys.platform)
    if platform in ("linux", "macos"):
        from unix import prepare
        prepare(sdk, output, platform)
        return
    if platform != "windows":
        parser.error("Unsupported host platform")
    nm, ar, gcc = (mingw / "bin" / f"{tool}.exe" for tool in ("nm", "ar", "gcc"))
    original = mingw / "lib/libucrt.a"
    archive = output / "libucrt-nativeaot.a"
    shutil.copy2(original, archive)
    # MSVC's static startup and exception runtime already provide these symbols.
    # Keep the UCRT imports for functions such as setjmp/longjmp that MinGW needs,
    # without replacing the MSVC implementations of its runtime internals.
    owned_by_msvc = {"__report_gsfailure", "__DestructExceptionObject",
                     "_IsExceptionObjectToBeDestroyed", "__current_exception",
                     "__current_exception_context", "__AdjustPointer",
                     "__FrameUnwindFilter", "__NLG_Return2", "__CxxFrameHandler3",
                     "__std_type_info_compare"}
    symbols = subprocess.check_output([nm, "-A", "--defined-only", original], text=True)
    members = set()
    for line in symbols.splitlines():
        match = re.search(r":([^:]+):[0-9a-fA-F]+\s+\w\s+(\S+)$", line)
        if match and match[2] in owned_by_msvc:
            members.add(match[1])
    if members:
        subprocess.run([ar, "d", archive, *sorted(members)], check=True)
    print("Excluded MSVC-owned import members:", ", ".join(sorted(members)))

    if not (sdk / "lib/libavcodec.a").is_file():
        raise FileNotFoundError("The SDK must contain lib/libavcodec.a")
    libraries = sorted((sdk / "lib").glob("*.a"))
    libraries += [mingw / "lib" / name for name in
                 ("libmingwex.a", "libstdc++.a", "libwinpthread.a", "libssp.a", "libiconv.a", "libatomic.a")]
    for name in ("libgcc.a", "libgcc_eh.a"):
        libraries.append(Path(subprocess.check_output([gcc, f"-print-file-name={name}"], text=True).strip()))
    libraries.append(archive)
    project = ET.Element("Project")
    items = ET.SubElement(project, "ItemGroup")
    for library in libraries:
        if not library.is_file():
            raise FileNotFoundError(library)
        if library.stat().st_size == 8:  # MSVC rejects an empty GNU archive.
            continue
        normalized = output / "lib" / library.name
        changed = normalize_archive(library, normalized)
        print(f"{library.name}: normalized {changed} COMDAT definitions")
        ET.SubElement(items, "NativeLibrary", Include=str(normalized.resolve()))
    ET.indent(project)
    props = output / "NativeLibraries.props"
    xml = ET.tostring(project, encoding="utf-8", xml_declaration=True)
    if not props.is_file() or props.read_bytes() != xml:
        props.write_bytes(xml)
    print("Prepared NativeAOT link support:", output)


if __name__ == "__main__":
    main()
