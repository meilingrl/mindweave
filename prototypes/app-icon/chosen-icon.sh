#!/bin/bash
# The chosen drawing as a macOS icon (prototype): screenshots/chosen.iconset and chosen.icns.
#   node prototypes/app-icon/brain.mjs && prototypes/app-icon/chosen-icon.sh
set -euo pipefail
cd "$(dirname "$0")/screenshots"
for v in full mid small; do qlmanage -t -s 1024 -o . "chosen-$v.svg" >/dev/null 2>&1; done
rm -rf chosen.iconset; mkdir chosen.iconset
make() { sips -Z "$2" "chosen-$1.svg.png" --out "chosen.iconset/$3" >/dev/null; }
make small 16 icon_16x16.png
make mid 32 icon_16x16@2x.png
make mid 32 icon_32x32.png
make mid 64 icon_32x32@2x.png
make full 128 icon_128x128.png
make full 256 icon_128x128@2x.png
make full 256 icon_256x256.png
make full 512 icon_256x256@2x.png
make full 512 icon_512x512.png
make full 1024 icon_512x512@2x.png
iconutil -c icns chosen.iconset -o chosen.icns
echo "wrote $(pwd)/chosen.icns"
