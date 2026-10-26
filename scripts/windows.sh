#!/usr/bin/env bash
# Drives the Windows 11 test machine over SSH (Tailscale). Key-based login only; never prompts for a password.
#
#   scripts/windows.sh sync      copy the current working tree (tracked + untracked, not ignored) to the machine
#   scripts/windows.sh test      sync, then run the full test suite there
#   scripts/windows.sh publish   sync, then publish a self-contained Mindweave.exe for interactive use
#   scripts/windows.sh exec CMD  run a PowerShell command inside the remote checkout
#
# Environment: MINDWEAVE_WIN_HOST (default win-home), MINDWEAVE_WIN_DIR (default E:/mindweave-claude-auto),
# MINDWEAVE_WIN_SOCKS (default 127.0.0.1:7898). This Mac reaches the tailnet through a userspace Tailscale
# instance that exposes only a loopback SOCKS proxy, so SSH goes through it; set it empty to connect directly.
set -euo pipefail

HOST="${MINDWEAVE_WIN_HOST:-win-home}"
DIR="${MINDWEAVE_WIN_DIR:-E:/mindweave-claude-auto}"
SRC="$DIR/src-snapshot"
ROOT="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
SOCKS="${MINDWEAVE_WIN_SOCKS-127.0.0.1:7898}"
SSH_OPTS=(-o BatchMode=yes -o ConnectTimeout=20 -o ServerAliveInterval=15 -o ServerAliveCountMax=4)
if [[ -n "$SOCKS" ]]; then
  SSH_OPTS+=(-o "ProxyCommand=nc -X 5 -x $SOCKS %h %p")
fi

# The tailnet path is relayed and occasionally drops new connections; retry only connection-level
# failures (ssh exit 255; keep-alives turn a stalled link into one), never a command that ran and failed.
retry_ssh() {
  local attempt status
  for attempt in 1 2 3 4 5 6; do
    "$@" && return 0
    status=$?
    [[ $status -eq 255 ]] || return $status
    echo "connection failed (attempt $attempt), retrying..." >&2
    sleep 2
  done
  return 255
}

remote() {
  # Encoded so quoting survives cmd.exe and PowerShell unchanged.
  local script="\$ErrorActionPreference='Stop'; \$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; \$env:DOTNET_CLI_UI_LANGUAGE='en'; \$env:DOTNET_NOLOGO='1'; $1"
  local encoded
  encoded="$(printf '%s' "$script" | iconv -t UTF-16LE | base64 | tr -d '\n')"
  retry_ssh ssh "${SSH_OPTS[@]}" "$HOST" "powershell -NoProfile -NonInteractive -EncodedCommand $encoded"
}

sync() {
  retry_ssh sync_once
}

sync_once() {
  # Sources are replaced wholesale so deleted files do not linger; build output lives outside the snapshot.
  # A transfer cut off mid-stream can leave its tar running and holding files; stop only those for this folder.
  remote "Get-CimInstance Win32_Process -Filter \"Name='tar.exe'\" | Where-Object { \$_.CommandLine -like '*$SRC*' } | ForEach-Object { Stop-Process -Id \$_.ProcessId -Force }; if (Test-Path '$SRC') { Remove-Item -Recurse -Force '$SRC' }; New-Item -ItemType Directory -Force '$SRC' | Out-Null"
  # Working tree as it is now: tracked files that still exist plus untracked, non-ignored ones.
  local files expected actual
  files="$(cd "$ROOT" && git ls-files -z --cached --others --exclude-standard -- . ':!prototypes' ':!docs/reviews' \
    | perl -0ne 'chomp; print "$_\n" if -e $_')"
  expected="$(printf '%s\n' "$files" | wc -l | tr -d ' ')"
  (cd "$ROOT" && printf '%s\n' "$files" | tr '\n' '\0' \
    | COPYFILE_DISABLE=1 xargs -0 tar --no-xattrs --no-mac-metadata -cf -) \
    | ssh "${SSH_OPTS[@]}" "$HOST" "tar -xf - -C $SRC" || return 255
  # A relay drop can end the stream early while the remote tar still exits 0 with a partial tree.
  actual="$(remote "(Get-ChildItem -Recurse -File -Force '$SRC' | Measure-Object).Count" | tr -d '\r')"
  if [[ "$actual" != "$expected" ]]; then
    echo "sync incomplete: $actual of $expected files arrived" >&2
    return 255
  fi
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
