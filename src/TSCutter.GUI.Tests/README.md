# Running tests

English | [简体中文](README_CN.md)

Windows, macOS, and Linux use the same test project. The .NET 10 SDK is required.

```sh
dotnet test src/TSCutter.GUI.Tests -c Debug
```

Ordinary logic, NAL type parsing, and headless UI tests require no video samples or environment variables.
Native media integration tests report **Skipped** when a prerequisite is missing, identifying the missing fixtures or FFmpeg runtime and explaining how to prepare them.

## Prepare synthetic media once

Install Python 3 and an FFmpeg CLI with the `libx264`, `libx265`, and `dvbsub` encoders. With `ffmpeg` and `ffprobe` on PATH, run these commands from the repository root:

```sh
python tools/prepare-test-media.py
dotnet test src/TSCutter.GUI.Tests -c Debug
```

The same commands work in Windows PowerShell. On macOS/Linux, use `python3` if that is your Python command.
If the tools are not on PATH, specify their executable paths explicitly. Quote paths containing spaces. For example, on Windows:

```powershell
python tools/prepare-test-media.py --ffmpeg "D:\Media Tools\ffmpeg.exe" --ffprobe "D:\Media Tools\ffprobe.exe"
```

Fixtures are generated in `artifacts/test-media/`, which is ignored by Git; videos are not checked in. The test build copies generated fixtures to its output directory, where tests discover them automatically without sample variables. If preparation fails, resolve the missing tool or encoder and regenerate before running the integration tests.

You can also prepare selected groups:

```sh
python tools/prepare-test-media.py --group keyframes
python tools/prepare-test-media.py --group remux subtitles
```

| Group | Coverage |
| --- | --- |
| `keyframes` | H.264/HEVC, fixed and variable GOPs, dense GOPs, B frames, fractional frame rates, TS with nonzero PTS, HLG, short clips, and single frames |
| `remux` | MP4/MKV export, multiple audio tracks, text/PGS subtitles, fractional-frame-rate HEVC, chapters, attachments, cover art, HDR, and open GOPs |
| `subtitles` | Preview, local-window seeking, sparse/empty tracks, and track switching for mov_text, animated ASS, PGS, and DVB subtitles |

Each generator can also run independently. Omitting its directory argument uses the same default location. `--font` accepts a TTF/OTF font for subtitle attachment tests.
The generators prepare test data only. They do not download tools, change application settings, or re-encode media on each `dotnet test` run.

## FFmpeg runtime

Fixture generation uses the `ffmpeg` / `ffprobe` command-line tools. The C# decoding tests load the application's **FFmpeg 9 shared libraries**; `ffmpeg.exe` alone does not replace them.

Prepare the complete runtime from [FFmpegSharedLibraries](https://github.com/nilaoda/FFmpegSharedLibraries), matching the operating system and test process architecture. Automatic lookup includes:

- The test output directory: `src/TSCutter.GUI.Tests/bin/Debug/net10.0/`.
- The application output directory: `src/TSCutter.GUI/bin/Debug/net10.0/`, including its RID subdirectory.
- Directories specified by `TSCUTTER_FFMPEG_ROOT` / `FFMPEG_ROOT`.
- The system library directories supported by the application on macOS/Linux.

Release tests look in the corresponding `Release` directories. Windows uses DLLs, macOS uses dylibs, and Linux uses shared objects. Keep the complete package and its dependencies together; do not mix operating systems, CPU architectures, or ABI versions.

Tests reuse the runtime already prepared for the local application in the current build configuration. If it is stored elsewhere, specify its location in Windows PowerShell:

```powershell
$env:TSCUTTER_FFMPEG_ROOT = "D:\Tools\ffmpeg-runtime"
dotnet test src/TSCutter.GUI.Tests -c Debug
```

On macOS/Linux:

```sh
TSCUTTER_FFMPEG_ROOT=/path/to/ffmpeg-runtime dotnet test src/TSCutter.GUI.Tests -c Debug
```

If library files are found but loading fails, or their architecture or ABI is incompatible, the tests fail with a loading error rather than treating a broken runtime as a normal skip.

## Optional custom directories and real media

The default synthetic tests do not depend on the following environment variables. These overrides let you reuse existing directories or run checks that require real media:

| Variable | Purpose |
| --- | --- |
| `TSCUTTER_TEST_MEDIA_ROOT` | Optional synthetic media root containing `keyframes/`, `remux/`, and `subtitles/`; use with the preparation script's `--output-dir` |
| `TSCUTTER_REMUX_TEST_SAMPLES` | Legacy remux fixture directory override, taking precedence over the shared root |
| `TSCUTTER_SUBTITLE_TEST_SAMPLES` | Legacy subtitle fixture directory override, taking precedence over the shared root |
| `TSCUTTER_FFMPEG_TEST_SAMPLES` | Optional real codec sample directory containing `1080i.ts`, `4K.ts`, `AVS+.ts`, `AVS+_2.ts`, `AVS+_P.ts`, `AVS2.ts`, and `AVS3.ts`, for checks such as packet position metadata from specialized decoders |
| `TSCUTTER_DOVI_TEST_SAMPLE` | Optional Dolby Vision Profile 5 MP4 with video at track 0 and audio at track 1, for export and HDR preview checks |
| `TSCUTTER_AV3A_TEST_SAMPLE` | Optional Audio Vivid MP4 for AV3A export checks |
| `TSCUTTER_SUBTITLE_REAL_SAMPLE` | Optional MP4 with exactly one subtitle track and an active subtitle at 2.5 seconds |

Real media are not downloaded, generated, or checked in. If an override is unset, only the corresponding real-media tests are skipped; synthetic and ordinary tests remain available.
Keyframe tests use the shared `keyframes` directory without a separate sample directory variable.
`TSCUTTER_KEYFRAME_STRIP_CAPTURE` and `TSCUTTER_SUBTITLE_PREVIEW_IMAGE` are optional screenshot output locations, not prerequisites for running tests.
