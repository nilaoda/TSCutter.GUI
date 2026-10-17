#!/usr/bin/env python3
"""Generate release notices independently of the application build (standard library only)."""

import argparse
import json
import os
from pathlib import Path
import shutil
import tempfile


ROOT = Path(__file__).resolve().parents[1]
SEPARATOR = "============================================================"


def runtime_pack_directory(metadata_file):
    # ResolveFrameworkReferences supplies the exact package used by this publish.
    metadata = json.loads(metadata_file.read_text(encoding="utf-8-sig"))
    packs = [pack for pack in metadata["Items"]["ResolvedRuntimePack"]
             if pack.get("FrameworkName") == "Microsoft.NETCore.App"]
    if len(packs) != 1 or not packs[0].get("PackageDirectory"):
        raise ValueError("Expected exactly one resolved Microsoft.NETCore.App runtime pack")
    return Path(packs[0]["PackageDirectory"])


def generate_notices(repository, output, runtime_packs=None):
    notices = [
        ("Third-party licenses", repository / "THIRD_PARTY_LICENSES.txt"),
        ("TSCutter.GUI / FFmpegSharedLibraries — GPL v3", repository / "LICENSE"),
    ]

    if runtime_packs is not None:
        runtime = runtime_pack_directory(runtime_packs)
        notices.extend([
            ("dotnet/LICENSE.txt", runtime / "LICENSE.TXT"),
            ("dotnet/THIRD-PARTY-NOTICES.txt", runtime / "THIRD-PARTY-NOTICES.TXT"),
        ])

    summary = repository / "THIRD_PARTY_NOTICES.md"
    for path in [summary, *(path for _, path in notices)]:
        if not path.is_file():
            raise FileNotFoundError(f"Missing notice input: {path}")
        if path.resolve() == output.resolve():
            raise ValueError(f"Output must not overwrite a notice input: {path}")

    # Preserve upstream line endings, strip input BOMs, and write UTF-8 without BOM.
    # Replace only after success so a missing/unreadable input cannot truncate an existing file.
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", newline="\n",
                                         dir=output.parent, delete=False) as writer:
            temporary = Path(writer.name)

            def copy(path):
                with path.open(encoding="utf-8-sig", newline="") as reader:
                    shutil.copyfileobj(reader, writer, length=4096)

            copy(summary)
            for title, path in notices:
                writer.write(f"\n\n{SEPARATOR}\n{title}\n{SEPARATOR}\n\n")
                copy(path)
        temporary.chmod(0o644)
        os.replace(temporary, output)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True,
                        help="Destination THIRD_PARTY_NOTICES.txt in the package directory")
    parser.add_argument("--runtime-packs", type=Path,
                        help="MSBuild ResolvedRuntimePack JSON; required for self-contained releases")
    args = parser.parse_args()
    try:
        generate_notices(ROOT, args.output, args.runtime_packs)
    except (OSError, ValueError, KeyError) as error:
        parser.exit(1, f"Cannot generate third-party notices: {error}\n")
    print(f"Generated {args.output}")


if __name__ == "__main__":
    main()
