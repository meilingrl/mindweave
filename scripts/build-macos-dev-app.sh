#!/bin/sh
# Local development launcher only; uses dedicated test data and the installed .NET runtime.
set -eu
repository=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
app="$repository/artifacts/macos/Mindweave Codex.app"
test_data_root=${1:-"$repository/artifacts/s0-native"}
case "$(uname -s):$(uname -m)" in
    Darwin:arm64) rid=osx-arm64 ;;
    Darwin:x86_64) rid=osx-x64 ;;
    *) printf '%s\n' 'Unsupported host: this launcher requires macOS arm64 or x86_64.' >&2; exit 1 ;;
esac

require_closed_app() {
    python3 - "$app" <<'PY'
import pathlib, subprocess, sys
app = pathlib.Path(sys.argv[1])
executables = {str(app / 'Contents/MacOS' / name) for name in ('Launch', 'Mindweave.Desktop')}
for line in subprocess.check_output(['ps', '-axo', 'pid=,comm='], text=True).splitlines():
    process = line.strip().split(None, 1)
    if len(process) == 2 and process[1] in executables:
        sys.exit(f'Refusing to replace running app (PID {process[0]}). Quit {app} normally, then rebuild.')
PY
}

require_closed_app
cd "$repository"
mkdir -p "$repository/artifacts"
# The staging name deliberately has no .app suffix: it is not an active bundle.
staging=$(mktemp -d "$repository/artifacts/macos-dev-stage.XXXXXX")
preparing=yes
cleanup_failed_preparation() {
    status=$?
    if [ "$status" -ne 0 ] && [ "$preparing" = yes ]; then
        rm -rf -- "$staging"
    fi
}
trap cleanup_failed_preparation 0
mkdir -p "$staging/Contents/MacOS"
dotnet publish "$repository/src/Mindweave.Desktop/Mindweave.Desktop.csproj" \
    -c Release --nologo -r "$rid" --self-contained false -p:PublishTrimmed=false \
    -o "$staging/Contents/MacOS"
python3 - "$staging" "$test_data_root" "$(command -v dotnet)" <<'PY'
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
sh -n "$staging/Contents/MacOS/Launch"
# Publishing may take time; check the exact owned executable paths again.
require_closed_app
mkdir -p "$(dirname -- "$app")"
preparing=no
python3 - "$app" "$staging" "$repository/artifacts/macos-previous" <<'PY'
import datetime, os, pathlib, sys
app, staging, previous_root = map(pathlib.Path, sys.argv[1:])
previous = None
if app.exists():
    previous_root.mkdir(parents=True, exist_ok=True)
    stamp = datetime.datetime.now().strftime('%Y%m%dT%H%M%S%f')
    previous = previous_root / f'Mindweave Codex-{stamp}-{os.getpid()}.reference'
    app.rename(previous)
try:
    staging.rename(app)
except OSError:
    if previous is not None and not app.exists():
        previous.rename(app)
    print(f'Installation failed; prepared bundle retained at {staging}', file=sys.stderr)
    raise
if previous is not None:
    print(f'Preserved previous bundle: {previous}')
PY
printf 'Published framework-dependent %s development bundle:\n' "$rid"
printf '%s\n' "$app"
