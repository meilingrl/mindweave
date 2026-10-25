#!/usr/bin/env bash
# Drives the Windows 11 test machine over SSH (Tailscale). Key-based login only; never prompts for a password.
#
#   scripts/windows.sh sync      copy the current working tree (tracked + untracked, not ignored) to the machine
#   scripts/windows.sh test      sync, then run the full test suite there
#   scripts/windows.sh publish   sync, then publish a self-contained Mindweave.exe for interactive use
#   scripts/windows.sh exec CMD  run a PowerShell command inside the remote checkout
#
# Environment: MINDWEAVE_WIN_HOST (default win-home), MINDWEAVE_WIN_DIR (default E:/mindweave-claude-auto).
set -euo pipefail

HOST="${MINDWEAVE_WIN_HOST:-win-home}"
DIR="${MINDWEAVE_WIN_DIR:-E:/mindweave-claude-auto}"
SRC="$DIR/src-snapshot"
ROOT="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"

remote() {
  # Encoded so quoting survives cmd.exe and PowerShell unchanged.
  local script="\$ErrorActionPreference='Stop'; \$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; \$env:DOTNET_CLI_UI_LANGUAGE='en'; \$env:DOTNET_NOLOGO='1'; $1"
  local encoded
  encoded="$(printf '%s' "$script" | iconv -t UTF-16LE | base64 | tr -d '\n')"
  ssh -o BatchMode=yes -o ConnectTimeout=20 "$HOST" "powershell -NoProfile -NonInteractive -EncodedCommand $encoded"
}

sync() {
  # Sources are replaced wholesale so deleted files do not linger; build output lives outside the snapshot.
  remote "if (Test-Path '$SRC') { Remove-Item -Recurse -Force '$SRC' }; New-Item -ItemType Directory -Force '$SRC' | Out-Null"
  (cd "$ROOT" && git ls-files -z --cached --others --exclude-standard -- . ':!prototypes' ':!docs/reviews' \
    | COPYFILE_DISABLE=1 xargs -0 tar --no-xattrs --no-mac-metadata -cf -) \
    | ssh -o BatchMode=yes "$HOST" "tar -xf - -C $SRC"
  echo "synced $(cd "$ROOT" && git rev-parse --short HEAD)$(cd "$ROOT" && git diff --quiet HEAD || echo '+dirty') to $HOST:$SRC"
}

case "${1:-}" in
  sync) sync ;;
  test)
    sync
    remote "Set-Location '$SRC'; dotnet --version; dotnet test Mindweave.slnx -c Release 2>&1 | Select-String -Pattern 'error|warn|\[FAIL\]|Passed!|Failed!|Exception|Assert' ; exit \$LASTEXITCODE"
    ;;
  publish)
    sync
    remote "Set-Location '$SRC'; dotnet publish src/Mindweave.Desktop -c Release -r win-x64 --self-contained -o '$DIR/app' 2>&1 | Select-String -Pattern 'error|warn|->'; exit \$LASTEXITCODE"
    echo "run on Windows: $DIR/app/Mindweave.exe"
    ;;
  exec) shift; remote "Set-Location '$SRC'; $*" ;;
  *) sed -n '2,10p' "$0"; exit 64 ;;
esac
