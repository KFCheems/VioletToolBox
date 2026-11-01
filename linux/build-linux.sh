#!/usr/bin/env bash
# Build VioletToolBox Linux edition (Fedora / general x86_64 Linux).
#   ./build-linux.sh              framework-dependent build (needs .NET 10 runtime)
#   ./build-linux.sh --self-contained   bundle the runtime, no .NET install needed
set -euo pipefail

cd "$(dirname "$0")"

MODE="--self-contained false"
OUT="dist"
if [[ "${1:-}" == "--self-contained" ]]; then
    MODE="--self-contained true"
    OUT="dist-selfcontained"
fi

echo "==> dotnet publish (linux-x64, $MODE)"
dotnet publish src/VioletToolBox.Linux/VioletToolBox.Linux.csproj \
    -c Release -r linux-x64 $MODE -o "$OUT/VioletToolBox"

APPDIR="$OUT/AppDir/VioletToolBox.AppDir"
echo "==> assembling AppDir"
mkdir -p "$APPDIR/bin" "$APPDIR/share/icons/hicolor/256x256/apps" "$APPDIR/share/applications"
cp -r "$OUT/VioletToolBox/." "$APPDIR/bin/"
chmod +x "$APPDIR/bin/VioletToolBox"

# Icon: convert the upstream .ico to PNG when ImageMagick is available.
if command -v magick >/dev/null 2>&1; then
    magick src/VioletToolBox.Linux/Assets/logo2.ico[0] "$APPDIR/share/icons/hicolor/256x256/apps/violettoolbox.png"
elif command -v convert >/dev/null 2>&1; then
    convert "src/VioletToolBox.Linux/Assets/logo2.ico[0]" "$APPDIR/share/icons/hicolor/256x256/apps/violettoolbox.png"
else
    cp src/VioletToolBox.Linux/Assets/logo2.ico "$APPDIR/share/icons/hicolor/256x256/apps/violettoolbox.ico"
fi

sed "s|Exec=.*|Exec=$PWD/$APPDIR/bin/VioletToolBox|; s|Icon=.*|Icon=$PWD/$APPDIR/share/icons/hicolor/256x256/apps/violettoolbox.png|" \
    packaging/violettoolbox.desktop > "$APPDIR/share/applications/violettoolbox.desktop"

echo "==> done: $OUT/VioletToolBox/VioletToolBox"
echo "    run:            $OUT/VioletToolBox/VioletToolBox"
echo "    install (user): cp -r $OUT/VioletToolBox ~/.local/share/ && cp packaging/violettoolbox.desktop ~/.local/share/applications/"
