#!/bin/sh
# Builds the Mac app with the Command Line Tools' swiftc (no Xcode needed) and puts it
# in ~/Applications. Run from the repo: sh mac/build.sh
set -e
cd "$(dirname "$0")"
OUT="${TABPET_DEST:-$HOME/Applications}/TabPet.app"
mkdir -p build
swiftc -O -swift-version 5 -target arm64-apple-macos14.0 -framework AppKit -framework CoreAudio Sources/Pet.swift Sources/main.swift -o build/TabPet
rm -rf "$OUT"
mkdir -p "$OUT/Contents/MacOS"
cp Info.plist "$OUT/Contents/Info.plist"
cp build/TabPet "$OUT/Contents/MacOS/TabPet"
# the example character and its sounds
mkdir -p "$OUT/Contents/Resources"
cp -R ../characters "$OUT/Contents/Resources/characters"
codesign --force --sign - "$OUT" >/dev/null 2>&1 || true
echo "built: $OUT"
