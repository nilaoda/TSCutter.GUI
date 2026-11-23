#!/usr/bin/env python3
"""Check static FFmpeg dependencies and exercise the final native executable."""
import argparse
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import wave


def reject_gnu_initializers(executable):
    data = executable.read_bytes()
    if len(data) < 64 or data[:2] != b"MZ":
        raise RuntimeError("Invalid Windows PE executable")
    pe = struct.unpack_from("<I", data, 60)[0]
    if pe + 24 > len(data) or data[pe:pe + 4] != b"PE\0\0":
        raise RuntimeError("Invalid Windows PE header")
    count = struct.unpack_from("<H", data, pe + 6)[0]
    sections = pe + 24 + struct.unpack_from("<H", data, pe + 20)[0]
    if not count or sections + count * 40 > len(data):
        raise RuntimeError("Invalid Windows PE section table")
    names = [data[offset:offset + 8].rstrip(b"\0")
             for offset in range(sections, sections + count * 40, 40)]
    unsupported = [name.decode("ascii", errors="replace") for name in names
                   if name.startswith((b".ctors", b".dtors", b".init_ar", b".fini_ar"))]
    if unsupported:
        raise RuntimeError(f"GNU initializers will not run under MSVC startup: {unsupported}")


def dependency_names(metadata, platform):
    if platform == "windows":
        return re.findall(r"DLL Name:\s*(\S+)", metadata)
    if platform == "linux":
        return re.findall(r"\(NEEDED\).*?\[([^]]+)\]", metadata)
    return [line.strip().split()[0] for line in metadata.splitlines()[1:] if line.strip()]


def readobj_dependency_names(metadata):
    # llvm-readobj --coff-imports prints one "Name: <dll>" field per imported DLL;
    # required for ARM64 PE, which the MinGW x86_64 objdump cannot parse.
    return re.findall(r"^\s+Name:\s*(\S+)", metadata, re.MULTILINE)


def reject_sdk_shared_libraries(names, sdk):
    stems = {archive.stem.lower() for archive in (sdk / "lib").glob("*.a")}
    for name in names:
        base = Path(name).name.lower()
        if any(base.startswith(stem + ".") or base.startswith(stem + "-") for stem in stems):
            raise RuntimeError(f"FFmpeg SDK dependency is dynamically linked or shipped: {name}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", type=Path, required=True)
    parser.add_argument("--objdump", type=Path)
    parser.add_argument("--readobj", type=Path)
    parser.add_argument("--sdk", type=Path)
    args = parser.parse_args()
    platform = {"win32": "windows", "linux": "linux", "darwin": "macos"}.get(sys.platform)
    if platform is None:
        parser.error("Unsupported verification host")
    executable = args.executable.resolve()
    if platform == "windows":
        reject_gnu_initializers(executable)
        if args.objdump is None and args.readobj is None:
            parser.error("Windows verification requires --objdump or --readobj")
        if {entry.name for entry in executable.parent.iterdir()} != {executable.name}:
            raise RuntimeError("The Windows publish directory must contain only the executable before packaging notices.")
        if args.readobj is not None:
            command = [args.readobj, "--coff-imports", executable]
        else:
            command = [args.objdump, "-p", executable]
    else:
        if args.sdk is None or not (args.sdk / "lib/libavcodec.a").is_file():
            parser.error("Unix verification requires --sdk pointing to the static SDK")
        command = ["readelf", "-d", executable] if platform == "linux" else ["otool", "-L", executable]
    raw_imports = subprocess.check_output(command, text=True)
    imports = (readobj_dependency_names(raw_imports) if platform == "windows" and args.readobj is not None
               else dependency_names(raw_imports, platform))
    if platform == "windows" and not imports:
        raise RuntimeError("Could not parse the import table of the executable")
    if platform == "windows":
        system = {"advapi32.dll", "bcrypt.dll", "crypt32.dll", "d3d9.dll", "dxgi.dll", "gdi32.dll",
                  "iphlpapi.dll", "kernel32.dll", "ncrypt.dll", "ntdll.dll", "ole32.dll", "oleaut32.dll",
                  "shell32.dll", "user32.dll", "ws2_32.dll"}
        unexpected = [name for name in imports if name.lower() not in system
                      and not name.lower().startswith("api-ms-win-")]
        if not imports or unexpected:
            raise RuntimeError(f"Unexpected native imports: {unexpected}")
    else:
        shipped = [str(path) for path in executable.parent.rglob("*") if path.is_file()
                   and (".so" in path.name or path.suffix in (".dylib", ".dll"))]
        reject_sdk_shared_libraries([*imports, *shipped], args.sdk)
        if platform == "linux" and any(name.startswith(("libstdc++.", "libgcc_s.", "libatomic.")) for name in imports):
            raise RuntimeError("FFmpeg's GCC/C++ support must be statically linked on Linux")
    with tempfile.TemporaryDirectory(prefix="tscutter-static-") as directory:
        isolated = Path(directory) / "app" / executable.name
        shutil.copytree(executable.parent, isolated.parent, ignore=shutil.ignore_patterns("*.dbg", "*.pdb", "*.dSYM"))
        sample = Path(directory) / "sample.wav"
        with wave.open(str(sample), "wb") as output:
            output.setparams((1, 2, 48000, 0, "NONE", "not compressed"))
            output.writeframes(b"\0\0" * 4800)
        env = os.environ.copy()
        for name in ("TSCUTTER_FFMPEG_ROOT", "FFMPEG_ROOT", "TSCUTTER_FFMPEG_BRIDGE", "LD_LIBRARY_PATH",
                     "DYLD_LIBRARY_PATH", "DYLD_FALLBACK_LIBRARY_PATH"):
            env.pop(name, None)
        if platform == "windows":
            windows = env.get("SYSTEMROOT", "C:/Windows")
            env["PATH"] = os.pathsep.join((str(Path(windows) / "System32"), windows))
        else:
            env["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin"
        result = subprocess.run([isolated, "--verify-static-ffmpeg", sample], cwd=directory, env=env,
                                capture_output=True, text=True, encoding="utf-8", errors="replace",
                                timeout=60, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        print(result.stdout + result.stderr, end="")
        if result.returncode or "FFmpeg static runtime verified." not in result.stdout:
            raise RuntimeError(f"Static runtime smoke test failed with exit code {result.returncode}.")
    print(f"Verified {executable.name}: static FFmpeg dependencies, audio decode, H.264/HEVC encoding and subtitle initialization.")


if __name__ == "__main__":
    main()
