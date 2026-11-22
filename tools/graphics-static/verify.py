#!/usr/bin/env python3
"""Reject bundled graphics shared libraries and exercise the published program."""
import argparse
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", type=Path, required=True)
    parser.add_argument("--objdump", type=Path)
    args = parser.parse_args()
    executable = args.executable.resolve()
    if sys.platform == "win32":
        if not args.objdump:
            parser.error("Windows verification requires --objdump")
        command = [args.objdump, "-p", executable]
    elif sys.platform == "linux":
        command = ["readelf", "-d", executable]
    elif sys.platform == "darwin":
        command = ["otool", "-L", executable]
    else:
        parser.error("Unsupported verification host")
    imports = subprocess.check_output(command, text=True)
    shared = re.compile(r"(?:libSkiaSharp|libHarfBuzzSharp|libAvaloniaNative|av_libglesv2)(?:[.][0-9]+)*[.](?:so|dll|dylib)", re.I)
    shipped = "\n".join(path.name for path in executable.parent.rglob("*") if path.is_file())
    if shared.search(imports) or shared.search(shipped):
        raise RuntimeError("The published program still imports or ships a graphics shared library")
    with tempfile.TemporaryDirectory(prefix="static-graphics-") as directory:
        isolated = Path(directory) / "app" / executable.name
        shutil.copytree(executable.parent, isolated.parent,
                        ignore=shutil.ignore_patterns("*.dbg", "*.pdb", "*.dSYM"))
        env = os.environ.copy()
        for name in ("LD_LIBRARY_PATH", "DYLD_LIBRARY_PATH", "DYLD_FALLBACK_LIBRARY_PATH"):
            env.pop(name, None)
        if sys.platform == "win32":
            windows = env.get("SYSTEMROOT", "C:/Windows")
            env["PATH"] = os.pathsep.join((str(Path(windows) / "System32"), windows))
        else:
            env["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin"
        result = subprocess.run([isolated, "--verify-static-graphics"], cwd=directory, env=env,
                                capture_output=True, text=True, encoding="utf-8", errors="replace",
                                timeout=60, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        print(result.stdout + result.stderr, end="")
        if result.returncode or "Static graphics runtime verified." not in result.stdout:
            raise RuntimeError(f"Static graphics verification failed: {result.returncode}")


if __name__ == "__main__":
    main()
