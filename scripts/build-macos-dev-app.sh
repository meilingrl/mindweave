#!/bin/sh
# Local development launcher only; uses dedicated test data and the installed .NET runtime.
set -eu
repository=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
app="$repository/artifacts/macos/Mindweave Codex.app"
test_data_root=${1:-"$repository/artifacts/s0-native"}
cd "$repository"
dotnet build "$repository/src/Mindweave.Desktop/Mindweave.Desktop.csproj" -c Release --nologo
mkdir -p "$app/Contents/MacOS"
cp -R "$repository/src/Mindweave.Desktop/bin/Release/net10.0/." "$app/Contents/MacOS/"
python3 - "$app" "$test_data_root" "$(command -v dotnet)" <<'PY'
import pathlib, plistlib, shlex, sys
app, data = pathlib.Path(sys.argv[1]), sys.argv[2]
runtime = str(pathlib.Path(sys.argv[3]).resolve().parent)
with (app / 'Contents/Info.plist').open('wb') as stream:
    plistlib.dump({'CFBundleExecutable': 'Launch', 'CFBundleIdentifier': 'local.mindweave.codex-steady',
                  'CFBundleName': 'Mindweave Codex', 'CFBundleDisplayName': 'Mindweave Codex',
                  'CFBundlePackageType': 'APPL', 'CFBundleVersion': '0.1', 'NSHighResolutionCapable': True}, stream)
launcher = app / 'Contents/MacOS/Launch'
launcher.write_text('#!/bin/sh\nset -eu\nexport DOTNET_ROOT=' + shlex.quote(runtime) + '\n'
                    'macos=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)\n'
                    'exec "$macos/Mindweave.Desktop" --data-root ' + shlex.quote(data) + '\n')
launcher.chmod(0o755)
PY
printf '%s\n' "$app"
