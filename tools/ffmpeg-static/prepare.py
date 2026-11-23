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


def rebuild_ucrt_archive(ar, original, archive):
    """Replace the filtered archive without retaining members from earlier runs."""
    import tempfile

    with tempfile.TemporaryDirectory(dir=archive.parent) as extract_root:
        rebuilt = Path(extract_root) / archive.name
        listing = subprocess.check_output([ar, "t", original], text=True).split()
        stub_members = [m for m in listing if m.startswith("api-ms-win-crt-")]
        wanted = [m for m in listing if not m.startswith("api-ms-win-crt-")]
        print(f"Rebuilding {archive.name}: {len(listing)} members, "
              f"dropping {len(stub_members)} api-set import stubs")
        chunks = []
        pos = 0
        while pos < len(wanted):
            chunk = wanted[pos:pos + 200]
            chunk_dir = Path(extract_root) / f"chunk{pos // 200}"
            chunk_dir.mkdir()
            subprocess.run([ar, "x", str(original), *chunk], check=True, cwd=chunk_dir)
            chunks.append((chunk_dir, chunk))
            pos += 200
        if not chunks:
            raise RuntimeError("libucrt.a has no members to keep")
        first_chunk_dir, first_chunk = chunks[0]
        subprocess.run([ar, "rcs", str(rebuilt), *first_chunk], check=True, cwd=first_chunk_dir)
        for chunk_dir, chunk in chunks[1:]:
            subprocess.run([ar, "r", str(rebuilt), *chunk], check=True, cwd=chunk_dir)

        rebuilt.replace(archive)


def prepare_windows_arm64(mingw, sdk, output):
    """Prepare the CLANGARM64-built static SDK for MSVC-linked NativeAOT (win-arm64).

    LLVM emits MSVC-compatible COFF; GNU initializer metadata is normalized
    for the MSVC CRT. Runtime libraries come from the CLANGARM64 environment
    instead of MinGW GCC's (no libgcc/libstdc++ there).
    """
    nm, ar = (mingw / "bin" / f"{tool}.exe" for tool in ("nm", "ar"))
    clang = mingw / "bin" / "clang.exe"
    original = mingw / "lib/libucrt.a"
    archive = output / "libucrt-nativeaot.a"
    # Same MSVC-owned UCRT exclusions as the win-x64 preparation. mingw's
    # libucrt.a also contains api-ms-win-crt-* import-stub members that
    # conflict with NativeAOT's static CRT (LIBCMT), and llvm-ar's delete
    # command removes only the first member matching each name (the archive
    # holds dozens of identically-named stubs). Rebuild the archive from the
    # non-stub members via extract + re-create; llvm-ar rebuilds the symbol
    # index on every write.
    owned_by_msvc = {"__report_gsfailure", "__DestructExceptionObject",
                     "_IsExceptionObjectToBeDestroyed", "__current_exception",
                     "__current_exception_context", "__AdjustPointer",
                     "__FrameUnwindFilter", "__NLG_Return2", "__CxxFrameHandler3",
                     "__std_type_info_compare"}
    symbols = subprocess.check_output([nm, "-A", "--defined-only", original], text=True)
    members = set()
    for line in symbols.splitlines():
        # GNU nm prints "archive:member:offset T symbol"; the llvm-nm shim in
        # CLANGARM64 prints "archive:member: T symbol" without the offset.
        match = re.match(r"^.*[:\[]([^:\]]+):\s*[0-9a-fA-F]*\s+\w\s+(\S+)\s*$", line)
        if match and match.group(2) in owned_by_msvc:
            members.add(match.group(1))
    print("Excluded MSVC-owned import members:", ", ".join(sorted(members)))

    # Rebuild libucrt-nativeaot.a from the non-import-stub members: extract
    # everything except api-ms-win-crt-* stubs, then re-create the archive
    # (llvm-ar writes a fresh symbol index on every create/insert).
    if not (sdk / "lib/libavcodec.a").is_file():
        raise FileNotFoundError("The SDK must contain lib/libavcodec.a")
    rebuild_ucrt_archive(ar, original, archive)
    # The mingw-w64 CRT's Control Flow Guard support object conflicts with
    # MSVC's LIBCMT (NativeAOT links LIBCMT); delete it from the copy.
    mingwex_copy = output / "lib" / "libmingwex.a"
    (output / "lib").mkdir(parents=True, exist_ok=True)
    shutil.copy2(mingw / "lib/libmingwex.a", mingwex_copy)
    mingwex_members = subprocess.check_output([ar, "t", mingwex_copy], text=True).split()
    cfguard = [m for m in mingwex_members if "cfguard_support" in m]
    if cfguard:
        subprocess.run([ar, "d", mingwex_copy, *cfguard], check=True)
        print("Deleted MSVC-conflicting CFG members from libmingwex:", ", ".join(cfguard))

    libraries = sorted((sdk / "lib").glob("*.a"))
    # CLANGARM64 runtime support: winpthread/iconv/mingwex from the mingw-w64
    # CRT, libc++ with the static libc++abi/libunwind baked in, and LLVM's
    # compiler builtins for aarch64 intrinsics helpers. libssp is skipped:
    # no SDK object references the stack-protector symbols and its archive
    # (built by the mingw-w64 CRT package) is not MSVC-readable.
    libraries += [mingw / "lib" / name for name in
                 ("libwinpthread.a", "libiconv.a", "libc++.a", "libunwind.a")]
    libraries.append(mingwex_copy)
    for name in ("libclang_rt.builtins-aarch64.a", "libatomic.a"):
        resolved = Path(subprocess.check_output([clang, f"-print-file-name={name}"], text=True).strip())
        if resolved.is_file():
            libraries.append(resolved)
        else:
            print(f"Skipping unavailable optional runtime archive: {name}")
    libraries.append(archive)

    # Some SDK sources call mingw-specific POSIX names (fseeko64/ftello64/
    # lseek64/strcasecmp/strncasecmp/stat64/fstat64) directly. In mingw's
    # libucrt.a those names live only in api-set import stubs, which the
    # rebuild above drops for NativeAOT's static CRT (LIBCMT provides none of
    # them). Provide forwarders to the underscore names the static CRT does
    # provide, matching the mingw <stdio.h>/<io.h>/<sys/stat.h> declarations
    # exactly (fseeko64 returns int); compiled with the same clangarm64 clang
    # as the SDK.
    compat_source = output / "compat-mingw.c"
    compat_source.write_text(
        "#include <stdio.h>\n"
        "#include <io.h>\n"
        "#include <string.h>\n"
        "#include <sys/stat.h>\n"
        "int fseeko64(FILE *stream, long long offset, int whence)\n"
        "{\n"
        "    return _fseeki64(stream, offset, whence);\n"
        "}\n"
        "long long ftello64(FILE *stream)\n"
        "{\n"
        "    return _ftelli64(stream);\n"
        "}\n"
        "long long lseek64(int fd, long long offset, int whence)\n"
        "{\n"
        "    return _lseeki64(fd, offset, whence);\n"
        "}\n"
        "int strcasecmp(const char *a, const char *b)\n"
        "{\n"
        "    return _stricmp(a, b);\n"
        "}\n"
        "int strncasecmp(const char *a, const char *b, size_t n)\n"
        "{\n"
        "    return _strnicmp(a, b, n);\n"
        "}\n"
        "int stat64(const char *path, struct stat64 *buf)\n"
        "{\n"
        "    return _stat64(path, (struct _stat64 *)buf);\n"
        "}\n"
        "int fstat64(int fd, struct stat64 *buf)\n"
        "{\n"
        "    return _fstat64(fd, (struct _stat64 *)buf);\n"
        "}\n",
        encoding="ascii")
    compat_obj = output / "compat-mingw.obj"
    subprocess.run([clang, "-O2", "-c", str(compat_source), "-o", str(compat_obj)], check=True)
    print("Prepared mingw CRT forwarders:", compat_obj)

    project = ET.Element("Project")
    items = ET.SubElement(project, "ItemGroup")
    for library in libraries:
        if not library.is_file():
            raise FileNotFoundError(library)
        if library.stat().st_size == 8:  # MSVC rejects an empty GNU archive.
            continue
        normalized = output / "lib" / library.name
        changed = normalize_archive(library, normalized)
        print(f"{library.name}: normalized {changed} COFF metadata records")
        ET.SubElement(items, "NativeLibrary", Include=str(normalized.resolve()))
    ET.SubElement(items, "NativeLibrary", Include=str(compat_obj.resolve()))
    ET.indent(project)
    props = output / "NativeLibraries.props"
    xml = ET.tostring(project, encoding="utf-8", xml_declaration=True)
    if not props.is_file() or props.read_bytes() != xml:
        props.write_bytes(xml)
    print("Prepared NativeAOT link support:", output)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mingw", type=Path, default=Path("C:/msys64/mingw64"))
    parser.add_argument("--sdk", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--rid", choices=("win-x64", "win-arm64"), default="win-x64")
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
    if args.rid == "win-arm64":
        prepare_windows_arm64(mingw, sdk, output)
        return
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
        print(f"{library.name}: normalized {changed} COFF metadata records")
        ET.SubElement(items, "NativeLibrary", Include=str(normalized.resolve()))
    ET.indent(project)
    props = output / "NativeLibraries.props"
    xml = ET.tostring(project, encoding="utf-8", xml_declaration=True)
    if not props.is_file() or props.read_bytes() != xml:
        props.write_bytes(xml)
    print("Prepared NativeAOT link support:", output)


if __name__ == "__main__":
    main()
