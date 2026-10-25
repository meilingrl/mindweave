#!/bin/bash
set -euo pipefail
spike_root="$(cd "$(dirname "$0")/.." && pwd)"
app_path="$spike_root/artifacts/Mindweave LiveMarkdown Spike.app"
dotnet publish "$spike_root/src/LiveMarkdownSpike/LiveMarkdownSpike.csproj" -c Release -r osx-arm64 --self-contained true -o "$spike_root/artifacts/publish" --nologo
mkdir -p "$app_path/Contents/MacOS" "$app_path/Contents/Resources"
cp -R "$spike_root/artifacts/publish/." "$app_path/Contents/MacOS/"
cat > "$app_path/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
 <key>CFBundleIdentifier</key><string>local.mindweave.livemarkdown-spike</string>
 <key>CFBundleName</key><string>Mindweave LiveMarkdown Spike</string>
 <key>CFBundleDisplayName</key><string>Mindweave LiveMarkdown Spike</string>
 <key>CFBundleExecutable</key><string>Mindweave.LiveMarkdownSpike</string>
 <key>CFBundlePackageType</key><string>APPL</string>
 <key>CFBundleShortVersionString</key><string>0.1.0</string>
 <key>CFBundleVersion</key><string>1</string>
 <key>NSHighResolutionCapable</key><true/>
 <key>NSPrincipalClass</key><string>NSApplication</string>
 <key>LSMinimumSystemVersion</key><string>12.0</string>
</dict></plist>
PLIST
chmod +x "$app_path/Contents/MacOS/Mindweave.LiveMarkdownSpike"
codesign --force --deep --sign - "$app_path"
printf '%s\n' "$app_path"
