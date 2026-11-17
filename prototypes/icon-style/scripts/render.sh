#!/usr/bin/env bash
# Renders the prototype's screenshots with headless Chrome (macOS path; set CHROME to override).
# Usage: prototypes/icon-style/scripts/render.sh
set -euo pipefail
cd "$(dirname "$0")/.."
CHROME="${CHROME:-/Applications/Google Chrome.app/Contents/MacOS/Google Chrome}"
OUT=screenshots
mkdir -p "$OUT"
page="file://$PWD/index.html"

shot() { # name query width height scale
  "$CHROME" --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor="$5" \
    --window-size="$3,$4" --screenshot="$OUT/$1.png" "$page?$2" >/dev/null 2>&1
  echo "$OUT/$1.png"
}

for t in light dark; do
  shot "sheet-$t"        "view=sheet&theme=$t"  1180 1680 2
  shot "sheet-$t-1x"     "view=sheet&theme=$t"  1180 1680 1
  shot "mocks-$t"        "view=mocks&theme=$t"  1240 1960 2
  shot "closeup-$t"      "view=closeup&theme=$t" 1500 1360 2
done
shot "grid-a"            "view=grid&dir=a"      900 720 2
shot "grid-b"            "view=grid&dir=b"      900 720 2
shot "grid-current"      "view=grid&dir=cur"    900 720 2
