# Native audio output bridge

English | [简体中文](README_CN.md)

`libtscutter_audio` sends interleaved float32 PCM to the default audio device.
Demuxing, decoding, downmixing and resampling use the application's existing FFmpeg
runtime, including swresample. The bridge does not embed or build FFmpeg.

## Contents

| File | Purpose |
| --- | --- |
| `miniaudio.h` | Vendored miniaudio 0.11.25 from https://github.com/mackron/miniaudio; dual Unlicense/MIT license (see `LICENSE-miniaudio.txt`) |
| `tsaudio_bridge.c` | SPSC ring buffer, device callback and consumed-frame counter for the audio clock |

The `tsaudio_*` C ABI provides device lifecycle, PCM writes, playback state,
consumed-frame and underflow counters, plus preview volume applied by the device
to buffered PCM. The native device callback performs bounded copies without calling
managed code.

## Build

AOT publishing statically links the bridge into the executable on Windows, macOS
and Linux via `DirectPInvoke`. Packaged AOT builds do not require a separate
`tscutter_audio.dll`, `libtscutter_audio.dylib` or `libtscutter_audio.so`.

Normal `dotnet build`, tests and non-AOT publishing use a shared bridge, because
managed assemblies cannot contain statically linked native code. Both library
forms are built automatically; only the shared form is copied for managed execution.
The target architecture comes from `RuntimeIdentifier`, or the SDK runtime identifier
when none is supplied. Build artifacts are separated by target RID under `obj`.

- **macOS:** Xcode command line tools (`clang`); x64 and arm64 targets use `-arch`.
- **Linux:** a C compiler (`cc`), an archiver (`ar`) and pthread, dl and math libraries.
  For cross-compilation, set `AudioBridgeCompiler` and, if needed, `AudioBridgeArchiver`
  to tools configured for the target architecture.
- **Windows:** Visual Studio C++ build tools, discovered through `vswhere`. The build
  selects `vcvarsall.bat` arguments for the target architecture; arm64 needs the
  corresponding MSVC toolchain installed.

Build on the target OS. Set `TSCutterSkipAudioBridge=true` to build without the bridge;
playback then continues silently if no bridge is available. End users of packaged
builds do not need a compiler.

## Updating miniaudio

Replace `miniaudio.h` and `LICENSE-miniaudio.txt` together and check API compatibility
in `tsaudio_bridge.c`. The bridge only enables device output, ring buffers and atomics;
miniaudio's decoding, encoding and engine features are disabled.
