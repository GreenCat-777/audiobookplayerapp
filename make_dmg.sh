#!/bin/bash
# Build the app (if needed) and package it into dist/audiobookplayer-macos.dmg.
# The DMG includes an Applications shortcut for drag-and-drop installation.
set -Eeuo pipefail

cd "$(dirname "$0")"

NAME="Audiobook Player"
APP="build/${NAME}.app"
DIST="dist"
DMG="${DIST}/audiobookplayer-macos.dmg"
STAGE="${DIST}/staging"
RW="${DIST}/rw.dmg"
BACKGROUND_SOURCE="Resources/dmg-background.png"
DEVICE=""

cleanup() {
    local status=$?
    if [[ -n "${DEVICE}" ]]; then
        hdiutil detach "${DEVICE}" -quiet >/dev/null 2>&1 || true
    fi
    if [[ -f "${RW}" ]]; then
        rm -f "${RW}"
    fi
    rm -rf "${STAGE}"
    if [[ ${status} -ne 0 ]]; then
        echo "DMG build failed (exit ${status})." >&2
    fi
    exit "${status}"
}
trap cleanup EXIT

# Build the app if it does not already exist.
if [[ ! -d "${APP}" ]]; then
    if [[ ! -x "./build_app.sh" ]]; then
        echo "Error: ${APP} is missing and ./build_app.sh is not executable or not found." >&2
        echo "Make it executable with: chmod +x build_app.sh" >&2
        exit 1
    fi
    NO_OPEN=1 ./build_app.sh
fi

if [[ ! -d "${APP}" ]]; then
    echo "Error: app build did not produce: ${APP}" >&2
    exit 1
fi

if [[ ! -f "${BACKGROUND_SOURCE}" ]]; then
    echo "Error: missing DMG background image: ${BACKGROUND_SOURCE}" >&2
    exit 1
fi

mkdir -p "${DIST}"
rm -rf "${STAGE}" "${DMG}" "${RW}"
mkdir -p "${STAGE}/.background"

cp -R "${APP}" "${STAGE}/"
ln -s /Applications "${STAGE}/Applications"
cp "${BACKGROUND_SOURCE}" "${STAGE}/.background/background.png"

# Detach a stale volume from a previous run, if present.
hdiutil detach "/Volumes/${NAME}" -quiet >/dev/null 2>&1 || true

# Create a writable image so Finder metadata/layout can be set before compression.
hdiutil create \
    -srcfolder "${STAGE}" \
    -volname "${NAME}" \
    -fs HFS+ \
    -format UDRW \
    -ov "${RW}" >/dev/null

DEVICE="$(hdiutil attach -readwrite -noverify -noautoopen "${RW}" |
    awk '/^\/dev\// { print $1; exit }')"

if [[ -z "${DEVICE}" ]]; then
    echo "Error: could not determine the mounted disk image device." >&2
    exit 1
fi

# Finder layout is cosmetic. If automation permission is denied, the DMG still works.
if ! osascript <<OSA
tell application "Finder"
    tell disk "${NAME}"
        open
        set current view of container window to icon view
        set toolbar visible of container window to false
        set statusbar visible of container window to false
        set bounds of container window to {200, 120, 860, 548}
        set opts to icon view options of container window
        set arrangement of opts to not arranged
        set icon size of opts to 128
        set background picture of opts to file ".background:background.png"
        set position of item "${NAME}.app" of container window to {165, 190}
        set position of item "Applications" of container window to {495, 190}
        update without registering applications
        delay 2
        close
        open
        delay 1
        close
    end tell
end tell
OSA
then
    echo "Note: Finder layout was skipped. Allow Terminal to control Finder in System Settings > Privacy & Security > Automation if you want the custom window layout."
fi

sync
hdiutil detach "${DEVICE}" -quiet >/dev/null
DEVICE=""

# Compress the writable image into the final read-only compressed DMG.
hdiutil convert "${RW}" \
    -format UDZO \
    -imagekey zlib-level=9 \
    -o "${DMG}" >/dev/null

rm -f "${RW}"
rm -rf "${STAGE}"
trap - EXIT

echo
echo "Created: ${DMG}"
open "${DIST}"
