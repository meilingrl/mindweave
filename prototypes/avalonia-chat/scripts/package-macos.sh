#!/bin/bash
set -euo pipefail
prototype_root="$(cd "$(dirname "$0")/.." && pwd)"
app_path="$prototype_root/artifacts/Mindweave Prototype.app"
dotnet publish "$prototype_root/src/Mindweave.Desktop/Mindweave.Desktop.csproj" -c Release -r osx-arm64 --self-contained true -o "$prototype_root/artifacts/publish" --nologo
mkdir -p "$app_path/Contents/MacOS" "$app_path/Contents/Resources"
cp -R "$prototype_root/artifacts/publish/." "$app_path/Contents/MacOS/"
cat > "$app_path/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleIdentifier</key><string>local.mindweave.prototype</string>
  <key>CFBundleName</key><string>Mindweave Prototype</string>
  <key>CFBundleDisplayName</key><string>Mindweave</string>
  <key>CFBundleExecutable</key><string>Mindweave</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>0.2.1</string>
  <key>CFBundleVersion</key><string>3</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSPrincipalClass</key><string>NSApplication</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
</dict></plist>
PLIST
chmod +x "$app_path/Contents/MacOS/Mindweave"
codesign --force --deep --sign - "$app_path"
printf '%s\n' "$app_path"
