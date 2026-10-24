#!/bin/sh
# Builds the plugin folder and the release zip:
#
#   dist/Pen Calibration/PenCalibration.dll
#   dist/Pen Calibration/App/<platform>/PenCalibrationApp(.app/.exe)
#   dist/PenCalibration.zip               (the same folder's contents, for the Plugin Manager)
#
# The app is self-contained, trimmed and compressed, so it runs without .NET installed.
# OTD's Plugin Manager takes one download per plugin, so every platform goes in one zip.
#
#   ./package.sh                 all platforms (osx-arm64, osx-x64, win-x64, linux-x64)
#   ./package.sh osx-arm64       only the listed platforms
#   ./package.sh --install       also copy the folder into OTD's plugin folder (macOS and Linux)
set -e
cd "$(dirname "$0")"

install=0
rids=""
for arg in "$@"; do
  case "$arg" in
    --install) install=1 ;;
    *) rids="$rids $arg" ;;
  esac
done
[ -z "$rids" ] && rids="osx-arm64 osx-x64 win-x64 linux-x64"

out="dist/Pen Calibration"
rm -rf dist
mkdir -p "$out/App"

dotnet build Plugin -c Release -o dist/plugin-build -nologo -v q
cp dist/plugin-build/PenCalibration.dll "$out/"
rm -rf dist/plugin-build

version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' App/PenCalibrationApp.csproj)

for rid in $rids; do
  dest="$out/App/$rid"
  dotnet publish App -c Release -r "$rid" --self-contained -nologo -v q \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none \
    -p:PublishTrimmed=true -p:EnableCompressionInSingleFile=true \
    -o "$dest"

  # On macOS, wrap the app in a .app bundle. The plugin opens it through LaunchServices,
  # which brings it to the front; a bare executable started by the daemon stays in the background.
  case "$rid" in
    osx-*)
      bundle="$dest/PenCalibrationApp.app"
      mkdir -p "$bundle/Contents/MacOS"
      mv "$dest/PenCalibrationApp" "$bundle/Contents/MacOS/"
      cat > "$bundle/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key><string>PenCalibrationApp</string>
  <key>CFBundleIdentifier</key><string>com.designbykivi.pencalibration</string>
  <key>CFBundleName</key><string>Pen Calibration</string>
  <key>CFBundleDisplayName</key><string>Pen Calibration</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST
      # Apple silicon refuses to run unsigned code; an ad-hoc signature is enough.
      if command -v codesign >/dev/null 2>&1; then
        codesign --force --deep --sign - "$bundle"
      fi
      ;;
  esac
  echo "Built app: $rid"
done

(cd "$out" && zip -q -r -9 ../PenCalibration.zip .)
echo "Packaged: $out"
echo "Release zip: dist/PenCalibration.zip ($(du -h dist/PenCalibration.zip | cut -f1))"
shasum -a 256 dist/PenCalibration.zip

if [ "$install" = 1 ]; then
  case "$(uname -s)" in
    Darwin) plugins="$HOME/Library/Application Support/OpenTabletDriver/Plugins" ;;
    Linux) plugins="${XDG_CONFIG_HOME:-$HOME/.config}/OpenTabletDriver/Plugins" ;;
    *) echo "--install only supports macOS and Linux; copy the folder manually." >&2; exit 1 ;;
  esac
  rm -rf "$plugins/Pen Calibration"
  mkdir -p "$plugins"
  cp -R "$out" "$plugins/"
  echo "Installed: $plugins/Pen Calibration (restart OpenTabletDriver to load it)"
fi
