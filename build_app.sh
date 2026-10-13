#!/bin/bash
# Builds a universal (Apple Silicon + Intel) "Audiobook Player.app" into ./build.
# Works with just the Command Line Tools: each architecture is built on its own,
# then merged with lipo (no xcbuild / full Xcode needed).
set -euo pipefail
cd "$(dirname "$0")"

MIN=14.0
BINS=()
for ARCH in arm64 x86_64; do
    TRIPLE="${ARCH}-apple-macosx${MIN}"
    echo "==> Building ${ARCH}"
    swift build -c release --triple "${TRIPLE}"
    BIN_DIR="$(swift build -c release --triple "${TRIPLE}" --show-bin-path)"
    BINS+=("${BIN_DIR}/AudiobookPlayer")
done

APP="build/Audiobook Player.app"
rm -rf "${APP}"
mkdir -p "${APP}/Contents/MacOS" "${APP}/Contents/Resources"
lipo -create "${BINS[@]}" -output "${APP}/Contents/MacOS/AudiobookPlayer"
cp Resources/Info.plist "${APP}/Contents/Info.plist"
cp Resources/AppIcon.icns "${APP}/Contents/Resources/AppIcon.icns"
codesign --force --deep --sign - "${APP}"

echo
lipo -info "${APP}/Contents/MacOS/AudiobookPlayer"
echo "Built: ${APP}"
echo "Tip: drag it into /Applications."
open build
