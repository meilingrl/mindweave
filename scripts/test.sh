#!/usr/bin/env bash
# Runs the tests at the size the moment needs (docs/design/testing.md).
#
#   scripts/test.sh quick [NAME...]       Core, Architecture and Infrastructure without fault injection (~15 s), plus
#                                         the desktop tests whose full name contains any NAME (none without NAME)
#   scripts/test.sh suite [DIR]           everything CI blocks on, with TRX results in DIR if given; the desktop tests
#                                         run as several processes at once (scripts/test-shards.py)
#   scripts/test.sh full                  suite (on macOS with the real Keychain tests), then the quarantined tests,
#                                         which only report
#   scripts/test.sh repeat N FILTER [PROJECT] [--load]
#                                         runs a dotnet test filter N times and counts failed runs; --load keeps all
#                                         cores busy meanwhile, the way a slow CI runner or a full parallel run does
#   scripts/test.sh timings [DIR]         full run with TRX results in DIR (default a temporary one), then the slowest
#                                         projects, classes and tests and those over the 3 s budget; also refreshes
#                                         the desktop class timings the shards are balanced by
#   scripts/test.sh quarantined           lists quarantined tests with their reason and date
#
# Before merging into the main line, `full` must pass; `quick` is for the edit-run loop only.
#
# MINDWEAVE_TEST_CONFIGURATION (default Debug) picks the build; MINDWEAVE_TEST_NO_BUILD=1 skips the build step;
# MINDWEAVE_TEST_SHARDS sets the number of desktop shards (default half the cores, 2 to 4).
set -euo pipefail

ROOT="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
SLN="$ROOT/Mindweave.slnx"
CORE="$ROOT/tests/Mindweave.Core.Tests"
ARCH="$ROOT/tests/Mindweave.Architecture.Tests"
INFRA="$ROOT/tests/Mindweave.Infrastructure.Tests"
DESKTOP="$ROOT/tests/Mindweave.Desktop.Tests"
NOT_QUARANTINED="Category!=Quarantine"
CONFIG="${MINDWEAVE_TEST_CONFIGURATION:-Debug}"
SHARD_TIMINGS="$DESKTOP/shard-timings.json"

LOAD_PIDS=()

die() { echo "test: $*" >&2; exit 1; }

# Looked up only when needed: `quick` and `repeat` need no Python.
py() {
  local exe
  exe="$(command -v python3 || command -v python)" || die "python3 (or python) is needed for this command"
  "$exe" -I "$@"
}

stop_load() { ((${#LOAD_PIDS[@]})) && kill "${LOAD_PIDS[@]}" 2>/dev/null; LOAD_PIDS=(); }

build() { [[ "${MINDWEAVE_TEST_NO_BUILD:-}" == 1 ]] || dotnet build "$SLN" -c "$CONFIG" -v q -nologo; }

shard_count() {
  local n="${MINDWEAVE_TEST_SHARDS:-}"
  if [[ -z "$n" ]]; then
    n=$(( $(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 2) / 2 ))
    # At least 2: the desktop tests spend much of their time waiting, so two shards help even on two cores.
    ((n < 2)) && n=2
    ((n > 4)) && n=4
  fi
  echo "$n"
}

quick() {
  build
  dotnet test "$CORE" --no-build -c "$CONFIG" --nologo
  dotnet test "$ARCH" --no-build -c "$CONFIG" --nologo
  dotnet test "$INFRA" --no-build -c "$CONFIG" --nologo --filter "Category!=FaultInjection&$NOT_QUARANTINED"
  if (($#)); then
    local filter=""
    for name in "$@"; do filter+="${filter:+|}FullyQualifiedName~$name"; done
    dotnet test "$DESKTOP" --no-build -c "$CONFIG" --nologo --filter "($filter)&$NOT_QUARANTINED"
  fi
}

# The projects other than Desktop run one after another (each is parallel inside); then the desktop shards.
suite() {
  local dir="${1:-}" trx=() results=() failed=0
  if [[ -n "$dir" ]]; then
    mkdir -p "$dir"
    trx=(--logger trx --results-directory "$dir")
    results=(--results "$dir")
  fi
  # Explicit: callers run `suite || ...`, and bash ignores `set -e` inside a function called that way.
  build || return 1
  # The test scripts' own checks: a report with Chinese in it arrives whole in any code page.
  py "$ROOT/scripts/script-checks.py" || failed=1
  for project in "$CORE" "$ARCH" "$INFRA"; do
    dotnet test "$project" --no-build -c "$CONFIG" --nologo --filter "$NOT_QUARANTINED" ${trx[@]+"${trx[@]}"} || failed=1
  done
  py "$ROOT/scripts/test-shards.py" run "$DESKTOP/bin/$CONFIG/net10.0/Mindweave.Desktop.Tests.dll" \
    --shards "$(shard_count)" --timings "$SHARD_TIMINGS" --hang 300 ${results[@]+"${results[@]}"} -- -trait- Category=Quarantine || failed=1
  ((failed == 0))
}

full() {
  local failed=0
  # The real key store is tried too where it raises no prompt: the macOS Keychain did not on 2026-11-27 (cross-platform-5
  # in docs/design/diagnostics-and-upkeep.md). Windows CI runs it; other systems have none and skip it.
  if [[ "$(uname -s)" == Darwin ]]; then export MINDWEAVE_CREDENTIAL_TESTS=1; fi
  suite || failed=1
  quarantined_run
  ((failed == 0))
}

quarantined_run() {
  echo "test: quarantined tests (reported, never blocking):"
  dotnet test "$SLN" --no-build -c "$CONFIG" --nologo --filter "Category=Quarantine" || echo "test: a quarantined test failed (see above)"
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
  if ((load)); then
    local cores
    cores="$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 4)"
    # Global, not local: the EXIT trap runs after this function has returned. The loops' output goes nowhere so a
    # pipe on this script's output closes when the script ends.
    trap stop_load EXIT INT TERM
    for ((i = 0; i < cores; i++)); do (while :; do :; done) >/dev/null 2>&1 & LOAD_PIDS+=($!); done
    echo "test: $cores busy loops running"
  fi

  local failed=0 log
  log="$(mktemp)"
  for ((i = 1; i <= n; i++)); do
    if dotnet test "$project" --no-build -c "$CONFIG" --nologo --filter "$filter" >"$log" 2>&1; then
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
  local dir="${1:-$(mktemp -d)}" passed=1
  mkdir -p "$dir"
  suite "$dir" >"$dir/suite.log" 2>&1 || passed=0
  grep -E "^(Passed|Failed)!|^shard |^test-shards|error" "$dir/suite.log" || true
  if ! grep -q "^test-shards: " "$dir/suite.log"; then
    die "the suite did not reach the desktop tests (build failed?); see $dir/suite.log"
  fi
  py "$ROOT/scripts/test-timings.py" "$dir"
  # Timings from a failed run still balance the shards, but a build failure never gets here.
  py "$ROOT/scripts/test-shards.py" record "$dir" --assembly Mindweave.Desktop.Tests --out "$SHARD_TIMINGS"
  ((passed)) || echo "test: the suite failed; see $dir/suite.log"
  echo "test: TRX files in $dir"
}

quarantined() {
  grep -rn --include='*.cs' -A0 '\[Quarantined(' "$ROOT/tests" | sed "s|$ROOT/||" || echo "test: nothing is quarantined"
}

cmd="${1:-}"
shift || true
case "$cmd" in
  quick) quick "$@" ;;
  suite) suite "$@" ;;
  full) full ;;
  repeat) repeat "$@" ;;
  timings) timings "$@" ;;
  quarantined) quarantined ;;
  *) sed -n '2,20p' "$0"; exit 2 ;;
esac
