# Release tools

English | [简体中文](README_CN.md)

## Application icons

The SVG source is [design/icons/app/logo.svg](../design/icons/app/logo.svg).
With CairoSVG and Pillow installed, generate the platform resources from the
repository root:

```sh
python3 tools/generate_app_icons.py --output-dir /tmp/tscutter-icons
```

Copy `logo.png`, `logo.ico`, and `logo.icns` into `src/TSCutter.GUI/Assets/`.
The generator renders at 2048 pixels and downsamples with premultiplied alpha
to preserve fine outlines at small sizes. It also exports individual PNG sizes
and a standard macOS iconset. Generated platform resources are checked in;
ordinary builds and CI packaging do not require Python or SVG rendering.

## Third-party notices

`generate_third_party_notices.py` combines `THIRD_PARTY_NOTICES.md`,
`THIRD_PARTY_LICENSES.txt`, the project `LICENSE`, and the .NET runtime notices
into one UTF-8 file. Upstream text and line endings are preserved, with input BOMs
removed. `THIRD_PARTY_LICENSES.txt` stores identical texts once and lists all
corresponding components in each section heading. Sdcb's original reference code
is kept separately in `design/reference/Sdcb.FFmpeg.VideoFrameConverter.cs.txt`.

The application projects do not import or run this tool. Ordinary `dotnet build`
and `dotnet publish` work independently of the repository's license documents and
Python. The shared `release-info` job generates the TXT once and uploads it as a
`third-party-notices` artifact. Windows, Linux, and macOS jobs download the same
file before archiving their packages. On macOS, the TXT goes alongside the
executable inside `TSCutter.GUI.app/Contents/MacOS`.

All three builds use the exact SDK version selected by the shared job. .NET's
runtime license texts for these targets have the same content (Windows uses
different line endings), so the shared job reads them from the Linux runtime
pack resolved by that SDK. The consolidated dependency licenses include the
Windows static graphics libraries as well as the shared cross-platform libraries.

For local self-contained packaging, query the same project's resolved runtime pack
using the same configuration and runtime identifier as the publish. For example,
from the repository root with Python 3 and the .NET SDK installed:

```sh
dotnet publish src/TSCutter.GUI -r linux-x64 -c Release -o artifact
dotnet msbuild src/TSCutter.GUI/TSCutter.GUI.csproj -nologo \
  -target:ResolveFrameworkReferences -property:Configuration=Release \
  -property:RuntimeIdentifier=linux-x64 -property:SelfContained=true \
  -getItem:ResolvedRuntimePack > runtime-packs.json
python3 tools/generate_third_party_notices.py \
  --runtime-packs runtime-packs.json --output artifact/THIRD_PARTY_NOTICES.txt
```

This appends the resolved runtime pack's `LICENSE.TXT` and
`THIRD-PARTY-NOTICES.TXT`. Missing runtime metadata or license files fail packaging
instead of silently falling back to older texts.

For a local preview of the static license texts, without runtime notices:

```sh
python3 tools/generate_third_party_notices.py --output artifact/THIRD_PARTY_NOTICES.txt
```

The script uses only the Python standard library. Run its focused checks with:

```sh
python3 -m unittest discover -s tools -p 'test_*.py'
```

## Disc image (ISO) test media

`generate-iso-test-samples.py` builds two synthetic disc images under
`artifacts/test-media/iso/` (Git-ignored):

* `bluray.iso` — a hand-authored BDMV structure (INDEX/MovieObject/MPLS/CLPI with
  real EP maps) plus four H.264 AC-3 M2TS clips. Titles cover a single-clip
  title, a 200 s multi-clip title (non-zero in-points, truncated out-points,
  clip order different from file order, the same clip referenced twice, a +300 s
  PTS base jump and variable bitrate), a PGS-subtitle title, and a 60 s title
  repeating the same PGS clip three times, plus a short playlist cut during
  subtitle display. Chapter marks cover PlayItem starts; an additional playlist
  enters a clip inside a GOP to verify that unsafe lossless joins are rejected.
* `dvd.iso` — dvdauthor-authored VIDEO_TS (two titles, one spanning three cells and two actual VOB files) with B frames, short GOPs and real VOB subpicture (SPU) streams, PGC palette and display/clear
  timings produced by `spumux`.

The matching `dvd/` and `bluray/` folders are retained alongside the images for
folder-input tests, including opening `VIDEO_TS` or `BDMV` directly.

Both images are built as ISO9660 + UDF hybrids: libdvdread's image path and
libbluray's udfread both read files through UDF.

### Requirements

| Component | Purpose | macOS | Debian/Ubuntu | Windows |
| --- | --- | --- | --- | --- |
| FFmpeg CLI with `libass`, `libx264`, `ac3`, `mpeg2video`, `mp2` | render text and encode clips | `brew install ffmpeg` | `apt install ffmpeg` | MSYS2 / gyan.dev build |
| `dvdauthor` (provides `spumux`) | author DVD-Video + VOB subtitles | `brew install dvdauthor` | `apt install dvdauthor` | MSYS2 / cygwin package |
| `mkisofs` (cdrtools) or `genisoimage` (cdrkit) with `-udf` | build the ISO images | `brew install cdrtools` | `apt install genisoimage` | cdrtools for Windows |

Use `mkisofs` or `genisoimage` for the tested ISO9660/UDF layout. This generator
does not validate `hdiutil` output, and `xorriso` does not write UDF.

The C# integration tests additionally need a compatible disc-enabled FFmpeg
runtime. The runtime build statically embeds libbluray 1.5+, libudfread,
libdvdread, libdvdnav and libdvdcss in avformat, which exports the APIs used by the GUI;
no separate disc shared libraries are needed. A development runtime may also
provide matching standalone libraries. Missing capabilities cause an explicit
skip; present but incompatible libraries fail rather than silently skipping.

### Command

```sh
python3 tools/prepare-test-media.py --group iso --ffmpeg ffmpeg --ffprobe ffprobe
dotnet test src/TSCutter.GUI.Tests -c Debug
```

`--skip-bluray` / `--skip-dvd` limit generation to one image. The generator wipes
its own `work/` directory before regenerating, so repeated runs never accumulate
extra dvdauthor title sets.

## Running tests

Windows, macOS and Linux use the same setup. With .NET 10 installed, run ordinary
logic and headless UI tests without sample environment variables:

```sh
dotnet test src/TSCutter.GUI.Tests -c Debug
```

To enable the synthetic media integration tests, install Python 3 and an FFmpeg
CLI with libx264, libx265 and dvbsub, then run:

```sh
python tools/prepare-test-media.py
dotnet test src/TSCutter.GUI.Tests -c Debug
```

Use `python3` if that is your Python command on macOS/Linux. The same commands
work in Windows PowerShell. `--ffmpeg` and `--ffprobe` accept explicit executable
paths, including paths with spaces; no shell-specific commands are used.
The C# tests also need the application's matching FFmpeg 9 shared runtime, which
is separate from the CLI used to generate media.

The common entry point supports `--group keyframes`, `--group remux subtitles`,
`--output-dir`, and an optional `--font` for subtitle font attachments. By default,
it prepares all three groups in `artifacts/test-media/`, which is ignored by Git.
The test build copies generated fixtures to its output directory and discovers
them automatically. Media are never downloaded or checked in, and ordinary test
runs do not regenerate them.

The individual `generate-keyframe-test-samples.py`,
`generate-remux-export-samples.py` and `generate-subtitle-preview-samples.py`
scripts remain usable; their directory argument is now optional and defaults to
the same shared location. Existing remux/subtitle sample overrides remain
compatible. Tests report whether missing fixtures, a missing runtime, or optional
real media caused a skip.

See the [test setup guide](../src/TSCutter.GUI.Tests/README.md) for runtime lookup,
Windows commands, fixture coverage, and the optional real-file checks for AVS,
Dolby Vision, Audio Vivid and subtitles.
