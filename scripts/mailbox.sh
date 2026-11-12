#!/usr/bin/env bash
# Mindweave mailbox, Mac side. Mac Claude and Windows Claude exchange messages through a folder on the Windows test
# machine (default E:/mindweave-mailbox), so the user doesn't have to carry text between machines that can't share
# a clipboard. The Mac reaches it over SSH the same way scripts/windows.sh does; Windows uses scripts/mailbox.ps1.
# Protocol and message format: docs/mailbox.md (copied into the mailbox as README.md by `init`).
#
#   scripts/mailbox.sh init                       create the folders; install README.md and mailbox.ps1 there
#   scripts/mailbox.sh send --slug S [--kind task] [--re ID] [--sha SHA] [--file F]   body from F or stdin
#   scripts/mailbox.sh list                       messages waiting for the Mac (to-mac/), oldest first
#   scripts/mailbox.sh read ID                    print one message (any folder)
#   scripts/mailbox.sh done ID                    move a handled message out of to-mac/ into done/
#   scripts/mailbox.sh status                     counts per folder; shows whether Windows has picked up tasks
#   scripts/mailbox.sh wait ID [SECONDS]          poll until a reply to ID arrives in to-mac/ (default 3600 s)
#
# Environment: MINDWEAVE_WIN_HOST (default win-home), MINDWEAVE_WIN_SOCKS (default 127.0.0.1:7898),
# MINDWEAVE_MAILBOX (default E:/mindweave-mailbox).
set -euo pipefail

HOST="${MINDWEAVE_WIN_HOST:-win-home}"
SOCKS="${MINDWEAVE_WIN_SOCKS-127.0.0.1:7898}"
BOX="${MINDWEAVE_MAILBOX:-E:/mindweave-mailbox}"
ROOT="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
SSH_OPTS=(-o BatchMode=yes -o ConnectTimeout=20 -o ServerAliveInterval=15 -o ServerAliveCountMax=4)
[[ -n "$SOCKS" ]] && SSH_OPTS+=(-o "ProxyCommand=nc -X 5 -x $SOCKS %h %p")

# Runs a PowerShell script on Windows. Retries only connection-level failures (exit 255), as scripts/windows.sh
# does, because the relayed tailnet link sometimes drops new connections. Nothing is sent on stdin: PowerShell
# started over SSH with -EncodedCommand never sees the end of a piped stdin and hangs.
retry() {
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
  local script="\$ErrorActionPreference='Stop'; \$ProgressPreference='SilentlyContinue'; \$Box='$BOX'; \$Utf8=New-Object Text.UTF8Encoding(\$false); $1"
  local encoded
  encoded="$(printf '%s' "$script" | iconv -t UTF-16LE | base64 | tr -d '\n')"
  retry ssh "${SSH_OPTS[@]}" "$HOST" "powershell -NoProfile -NonInteractive -EncodedCommand $encoded"
}

# Copies a local file into the mailbox: uploaded under a dot-named temp name, then renamed into place, so readers
# never see a half-written message.
put() { # local-file dir name
  retry scp -q "${SSH_OPTS[@]}" "$1" "$HOST:$BOX/$2/.$3.tmp"
  remote "Move-Item -Force -LiteralPath (Join-Path \$Box '$2/.$3.tmp') -Destination (Join-Path \$Box '$2/$3')"
}

valid_id() { [[ "$1" =~ ^[0-9]{8}-[0-9]{6}-[mw]-[a-z0-9-]+$ ]] || { echo "bad message id: $1" >&2; exit 2; }; }

cmd="${1:-}"; shift || true
case "$cmd" in
  init)
    remote "foreach (\$d in 'to-windows','to-mac','done') { New-Item -ItemType Directory -Force (Join-Path \$Box \$d) | Out-Null }; 'ok'"
    put "$ROOT/docs/mailbox.md" . README.md
    put "$ROOT/scripts/mailbox.ps1" . mailbox.ps1
    echo "mailbox ready at $HOST:$BOX"
    ;;
  send)
    kind=task slug="" re="" sha="" file=""
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --kind) kind="$2"; shift 2 ;; --slug) slug="$2"; shift 2 ;; --re) re="$2"; shift 2 ;;
        --sha) sha="$2"; shift 2 ;; --file) file="$2"; shift 2 ;; *) echo "unknown option $1" >&2; exit 2 ;;
      esac
    done
    [[ "$kind" =~ ^(task|report|question|answer|note)$ ]] || { echo "bad kind: $kind" >&2; exit 2; }
    [[ -n "$re" ]] && valid_id "$re"
    slug="$(printf '%s' "${slug:-message}" | tr '[:upper:]' '[:lower:]' | sed -E 's/[^a-z0-9-]+/-/g; s/^-+//; s/-+$//' | cut -c1-40 | sed -E 's/-+$//')"
    id="$(date +%Y%m%d-%H%M%S)-m-${slug:-message}"
    body="$(if [[ -n "$file" ]]; then cat "$file"; else cat; fi)"
    [[ -n "${body//[[:space:]]/}" ]] || { echo "empty message body" >&2; exit 2; }
    tmp="$(mktemp)"; trap 'rm -f "$tmp"' EXIT
    printf -- '---\nid: %s\nfrom: mac-claude\nto: windows-claude\nkind: %s\nre: %s\nsha: %s\ncreated: %s\n---\n\n%s\n' \
      "$id" "$kind" "$re" "$sha" "$(date +%Y-%m-%dT%H:%M:%S%z)" "$body" > "$tmp"
    put "$tmp" to-windows "$id.md"
    echo "sent $id"
    ;;
  list)
    remote "\$f=@(Get-ChildItem (Join-Path \$Box 'to-mac') -Filter *.md -File | Where-Object { \$_.Name -notlike '.*' } | Sort-Object Name); if (-not \$f.Count) { 'to-mac is empty' } else { foreach (\$x in \$f) { \$h=@{}; foreach (\$l in [IO.File]::ReadAllLines(\$x.FullName,\$Utf8)) { if (\$l -match '^(kind|re|sha):\s*(.*)$') { \$h[\$Matches[1]]=\$Matches[2] } }; '{0}  [{1}]  re={2}  sha={3}' -f \$x.BaseName,\$h.kind,\$h.re,\$h.sha } }" | tr -d '\r'
    ;;
  read)
    valid_id "${1:-}"
    remote "foreach (\$d in 'to-mac','to-windows','done') { \$p=Join-Path \$Box \"\$d/$1.md\"; if (Test-Path \$p) { [Convert]::ToBase64String([IO.File]::ReadAllBytes(\$p)); exit 0 } }; throw 'no message $1'" \
      | tr -d '\r' | base64 -d
    ;;
  done)
    valid_id "${1:-}"
    remote "\$p=Join-Path \$Box 'to-mac/$1.md'; if (-not (Test-Path \$p)) { throw '$1 is not in to-mac' }; Move-Item -LiteralPath \$p -Destination (Join-Path \$Box 'done/$1.md'); 'moved $1 to done'" | tr -d '\r'
    ;;
  status)
    remote "foreach (\$d in 'to-windows','to-mac','done') { \$f=@(Get-ChildItem (Join-Path \$Box \$d) -Filter *.md -File | Where-Object { \$_.Name -notlike '.*' } | Sort-Object Name); '{0,-11} {1,3}  newest: {2}' -f \$d,\$f.Count,(\$(if (\$f.Count) { \$f[-1].BaseName } else { '-' })) }" | tr -d '\r'
    ;;
  wait)
    valid_id "${1:-}"
    deadline=$(( $(date +%s) + ${2:-3600} ))
    while :; do
      found="$(remote "Get-ChildItem (Join-Path \$Box 'to-mac') -Filter *.md -File | Where-Object { \$_.Name -notlike '.*' -and (Select-String -LiteralPath \$_.FullName -Pattern '^re: $1\s*$' -Quiet) } | Sort-Object Name | ForEach-Object BaseName" | tr -d '\r' || true)"
      if [[ -n "$found" ]]; then echo "$found"; exit 0; fi
      (( $(date +%s) < deadline )) || { echo "no reply to $1 yet" >&2; exit 1; }
      sleep 60
    done
    ;;
  *) sed -n '2,16p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
esac
