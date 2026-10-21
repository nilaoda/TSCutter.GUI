# Release tools

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

## Running tests

Run the ordinary tests with .NET 10; no sample environment variables are required:

```sh
dotnet test src/TSCutter.GUI.Tests -c Debug
```

Tests requiring native media samples are optional integration tests. They are
reported as skipped when their sample variable is unset. The generators below
create small, reusable fixtures; running them also requires a local FFmpeg CLI
with the listed encoders. The application's shared libraries are used by the
tests themselves. Real-file checks accept separate sample paths and do not
require the generated fixture directories. Release builds do not run these tests.

## Remux export regression fixtures

Generate small MP4/MKV files with B frames, multiple audio tracks, mov_text/ASS
subtitles, chapters, attachments, cover art, and HDR10 using a local FFmpeg with
libx264 and libx265:

```sh
python3 tools/generate-remux-export-samples.py /tmp/tscutter-remux-fixtures
TSCUTTER_REMUX_TEST_SAMPLES=/tmp/tscutter-remux-fixtures \
  dotnet test src/TSCutter.GUI.Tests -c Release --filter FullyQualifiedName~RemuxExportServiceTests
```

The tests use the application's FFmpeg 9 shared libraries. Set
`TSCUTTER_FFMPEG_ROOT` if they are not in a default runtime directory. Optional
`TSCUTTER_DOVI_TEST_SAMPLE` points to a Dolby Vision Profile 5 MP4 containing a
video track at index 0 and audio at index 1. The optional test verifies both MP4
and MKV exports, comparing compressed video packet hashes and Dolby Vision
configuration. Optional `TSCUTTER_AV3A_TEST_SAMPLE` points to an Audio Vivid MP4;
with compatible keyframe boundaries for partial cuts. Its tests check audio
packet hashes and codec configuration in MP4 exports, merge three separate
ranges and files in queue order, and confirm that unsupported MKV exports fail
without creating output.
Generated fixtures and local samples are not committed.

## Subtitle preview regression fixtures

Generate MP4 mov_text, animated MKV ASS, MKV PGS and MPEG-TS DVB subtitle samples:

```sh
python3 tools/generate-subtitle-preview-samples.py /path/to/subtitle-fixtures
TSCUTTER_FFMPEG_ROOT=/path/to/ffmpeg-runtime \
TSCUTTER_SUBTITLE_TEST_SAMPLES=/path/to/subtitle-fixtures \
  dotnet test src/TSCutter.GUI.Tests --filter FullyQualifiedName~SubtitlePreviewTests
```

Pass `--font /path/to/font.ttf` to test MKV font attachments. The generator needs
FFmpeg with libx264 and the dvbsub encoder. The native tests use the packaged
FFmpeg 9 runtime with libass. They check subtitle timing, clearing, backward seeks,
ASS animation, resizing, rapid track changes and switching subtitles off.
Set `TSCUTTER_SUBTITLE_REAL_SAMPLE` to a local MP4 with exactly one subtitle track
and an active subtitle at 2.5 seconds to check a real file. This check is skipped
when that variable is unset. `TSCUTTER_SUBTITLE_PREVIEW_IMAGE` is only an optional
output path for saving the generated-fixture UI render; it is not a prerequisite.
