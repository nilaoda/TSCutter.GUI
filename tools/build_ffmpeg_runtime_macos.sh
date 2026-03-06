#!/usr/bin/env bash
set -euo pipefail

# Build a self-contained macOS arm64 FFmpeg runtime:
# - only FFmpeg's own shared dylibs are packaged
# - external third-party dylib dependencies are treated as build failures
# - decoders stay broad by default, while most encoders are disabled

FFMPEG_VERSION="${1:?usage: build_ffmpeg_runtime_macos.sh <ffmpeg-version> <gpl|lgpl> [work-root] }"
LICENSE_FLAVOR="${2:?usage: build_ffmpeg_runtime_macos.sh <ffmpeg-version> <gpl|lgpl> [work-root] }"
WORK_ROOT="${3:-$PWD/.ffmpeg-runtime-build}"

case "$LICENSE_FLAVOR" in
  gpl|lgpl)
    ;;
  *)
    echo "Unsupported license flavor: $LICENSE_FLAVOR" >&2
    exit 1
    ;;
esac

ARCHIVE_NAME="ffmpeg-$FFMPEG_VERSION.tar.xz"
SOURCE_URL="https://ffmpeg.org/releases/$ARCHIVE_NAME"
SOURCE_ROOT="$WORK_ROOT/src"
SOURCE_ARCHIVE="$SOURCE_ROOT/$ARCHIVE_NAME"
SOURCE_DIR="$SOURCE_ROOT/ffmpeg-$FFMPEG_VERSION"
INSTALL_ROOT="$WORK_ROOT/install"
PACKAGE_NAME="ffmpeg-runtime-osx-arm64-$LICENSE_FLAVOR-shared-$FFMPEG_VERSION"
PACKAGE_ROOT="$WORK_ROOT/package/$PACKAGE_NAME"
RUNTIME_ROOT="$PACKAGE_ROOT/runtimes/osx/native"
ARTIFACT_ROOT="$WORK_ROOT/artifacts"
ARTIFACT_PATH="$ARTIFACT_ROOT/$PACKAGE_NAME.zip"

mkdir -p "$SOURCE_ROOT" "$INSTALL_ROOT" "$RUNTIME_ROOT" "$ARTIFACT_ROOT"

if [[ ! -f "$SOURCE_ARCHIVE" ]]; then
  curl -L "$SOURCE_URL" -o "$SOURCE_ARCHIVE"
fi

rm -rf "$SOURCE_DIR"
tar -xf "$SOURCE_ARCHIVE" -C "$SOURCE_ROOT"

rm -rf "$INSTALL_ROOT" "$PACKAGE_ROOT"
mkdir -p "$INSTALL_ROOT" "$RUNTIME_ROOT"

pushd "$SOURCE_DIR" >/dev/null

CONFIGURE_FLAGS=(
  --prefix="$INSTALL_ROOT"
  --arch=arm64
  --target-os=darwin
  --cc=clang
  --enable-shared
  --enable-pthreads
  --disable-static
  --disable-programs
  --disable-doc
  --disable-debug
  --enable-pic
  --disable-autodetect
  --disable-ffplay
  --disable-network
  --disable-indevs
  --disable-outdevs
  --disable-devices
  --disable-encoders
  --enable-encoder=png,mjpeg,bmp
  --enable-videotoolbox
  --enable-audiotoolbox
  --enable-neon
)

if [[ "$LICENSE_FLAVOR" == "gpl" ]]; then
  CONFIGURE_FLAGS+=(--enable-gpl --enable-version3)
fi

./configure "${CONFIGURE_FLAGS[@]}"
make -j"$(sysctl -n hw.ncpu)"
make install

popd >/dev/null

cp "$INSTALL_ROOT"/lib/*.dylib "$RUNTIME_ROOT"/
cp "$SOURCE_DIR"/COPYING* "$PACKAGE_ROOT"/

for dylib in "$RUNTIME_ROOT"/*.dylib; do
  dylib_name="$(basename "$dylib")"
  install_name_tool -id "@loader_path/$dylib_name" "$dylib"
done

for dylib in "$RUNTIME_ROOT"/*.dylib; do
  while IFS= read -r dependency; do
    dependency_name="$(basename "$dependency")"
    dependency_local="$RUNTIME_ROOT/$dependency_name"
    if [[ -f "$dependency_local" ]]; then
      install_name_tool -change "$dependency" "@loader_path/$dependency_name" "$dylib"
    fi
  done < <(otool -L "$dylib" | tail -n +2 | awk '{print $1}')
done

{
  echo "FFmpeg version: $FFMPEG_VERSION"
  echo "License flavor: $LICENSE_FLAVOR"
  echo "Built on: $(date -u +"%Y-%m-%dT%H:%M:%SZ")"
  echo
  echo "Bundled dylibs:"
  find "$RUNTIME_ROOT" -maxdepth 1 -type f -name '*.dylib' -print | sort
  echo
  echo "Dependency report:"
  for dylib in "$RUNTIME_ROOT"/*.dylib; do
    echo "## $(basename "$dylib")"
    otool -L "$dylib"
    echo
  done
} >"$PACKAGE_ROOT/runtime-manifest.txt"

for dylib in "$RUNTIME_ROOT"/*.dylib; do
  while IFS= read -r dependency; do
    dependency_name="$(basename "$dependency")"
    case "$dependency" in
      @loader_path/*|/usr/lib/*|/System/Library/*)
        ;;
      *)
        echo "Unexpected external dependency in $(basename "$dylib"): $dependency" >&2
        exit 1
        ;;
    esac

    if [[ "$dependency" == @loader_path/* && ! -f "$RUNTIME_ROOT/$dependency_name" ]]; then
      echo "Missing bundled dependency for $(basename "$dylib"): $dependency_name" >&2
      exit 1
    fi
  done < <(otool -L "$dylib" | tail -n +2 | awk '{print $1}')
done

(
  cd "$WORK_ROOT/package"
  zip -qry "$ARTIFACT_PATH" "$PACKAGE_NAME"
)
echo "Created artifact: $ARTIFACT_PATH"
