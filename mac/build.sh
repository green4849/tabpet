#!/bin/sh
# Builds the Mac app with the Command Line Tools' swiftc (no Xcode needed) and puts it
# in ~/Applications. Run from the repo: sh mac/build.sh
set -e
cd "$(dirname "$0")"
OUT="${TABPET_DEST:-$HOME/Applications}/TabPet.app"
mkdir -p build
swiftc -O -swift-version 5 -target arm64-apple-macos14.0 -framework CoreAudio Sources/Pet.swift Sources/Todo.swift Sources/main.swift -o build/TabPet
# quit a running copy first, or the new one would see it and exit at once
pkill -x TabPet 2>/dev/null || true
rm -rf "$OUT"
mkdir -p "$OUT/Contents/MacOS"
cp Info.plist "$OUT/Contents/Info.plist"
cp build/TabPet "$OUT/Contents/MacOS/TabPet"
# the example character and its sounds
mkdir -p "$OUT/Contents/Resources"
cp -R ../characters "$OUT/Contents/Resources/characters"
codesign --force --sign - "$OUT"   # ad-hoc signature, needed on Apple Silicon
echo "built: $OUT"
