#!/usr/bin/env bash
# Runs the tests at the size the moment needs (docs/design/testing.md).
#
#   scripts/test.sh quick [NAME...]       Core, Architecture and Infrastructure without fault injection (~15 s), plus
#                                         the desktop tests whose full name contains any NAME (none without NAME)
#   scripts/test.sh full                  everything CI runs; quarantined tests run after it and only report
#   scripts/test.sh repeat N FILTER [PROJECT] [--load]
#                                         runs a dotnet test filter N times and counts failed runs; --load keeps all
#                                         cores busy meanwhile, the way a slow CI runner or a full parallel run does
#   scripts/test.sh timings [DIR]         full run with TRX results in DIR (default a temporary one), then the slowest
#                                         projects, classes and tests and those over the 3 s budget
#   scripts/test.sh quarantined           lists quarantined tests with their reason and date
#
# Before merging into track/claude-auto, `full` must pass; `quick` is for the edit-run loop only.
set -euo pipefail

ROOT="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
SLN="$ROOT/Mindweave.slnx"
CORE="$ROOT/tests/Mindweave.Core.Tests"
ARCH="$ROOT/tests/Mindweave.Architecture.Tests"
INFRA="$ROOT/tests/Mindweave.Infrastructure.Tests"
DESKTOP="$ROOT/tests/Mindweave.Desktop.Tests"
NOT_QUARANTINED="Category!=Quarantine"

die() { echo "test: $*" >&2; exit 1; }

build() { dotnet build "$SLN" -v q -nologo; }

quick() {
  build
  dotnet test "$CORE" --no-build --nologo
  dotnet test "$ARCH" --no-build --nologo
  dotnet test "$INFRA" --no-build --nologo --filter "Category!=FaultInjection&$NOT_QUARANTINED"
  if (($#)); then
    local filter=""
    for name in "$@"; do filter+="${filter:+|}FullyQualifiedName~$name"; done
    dotnet test "$DESKTOP" --no-build --nologo --filter "($filter)&$NOT_QUARANTINED"
  fi
}

full() {
  build
  dotnet test "$SLN" --no-build --nologo --filter "$NOT_QUARANTINED"
  quarantined_run
}

quarantined_run() {
  echo "test: quarantined tests (reported, never blocking):"
  dotnet test "$SLN" --no-build --nologo --filter "Category=Quarantine" || echo "test: a quarantined test failed (see above)"
}

repeat() {
  local n="${1:-}" filter="${2:-}" project="$SLN" load=0
  [[ "$n" =~ ^[0-9]+$ && -n "$filter" ]] || die "usage: repeat N FILTER [PROJECT] [--load]"
  shift 2
  for arg in "$@"; do
    case "$arg" in
      --load) load=1 ;;
      *) project="$ROOT/$arg" ;;
    esac
  done

  build
  local pids=()
  if ((load)); then
    local cores
    cores="$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 4)"
    for ((i = 0; i < cores; i++)); do (while :; do :; done) & pids+=($!); done
    trap 'kill "${pids[@]}" 2>/dev/null || true' EXIT
    echo "test: $cores busy loops running"
  fi

  local failed=0 log
  log="$(mktemp)"
  for ((i = 1; i <= n; i++)); do
    if dotnet test "$project" --no-build --nologo --filter "$filter" >"$log" 2>&1; then
      echo "run $i: passed"
    else
      failed=$((failed + 1))
      echo "run $i: FAILED"
      grep -E "\[FAIL\]|Exception|Assert" "$log" | head -12 | sed 's/^/    /'
    fi
  done
  rm -f "$log"
  echo "test: $failed of $n runs failed"
  ((failed == 0))
}

timings() {
  local dir="${1:-$(mktemp -d)}"
  build
  dotnet test "$SLN" --no-build --nologo --filter "$NOT_QUARANTINED" --logger trx --results-directory "$dir" | grep -E "^(Passed|Failed)!" || true
  python3 -I "$ROOT/scripts/test-timings.py" "$dir"
  echo "test: TRX files in $dir"
}

quarantined() {
  grep -rn --include='*.cs' -A0 '\[Quarantined(' "$ROOT/tests" | sed "s|$ROOT/||" || echo "test: nothing is quarantined"
}

cmd="${1:-}"
shift || true
case "$cmd" in
  quick) quick "$@" ;;
  full) full ;;
  repeat) repeat "$@" ;;
  timings) timings "$@" ;;
  quarantined) quarantined ;;
  *) sed -n '2,15p' "$0"; exit 2 ;;
esac
