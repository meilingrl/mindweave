#!/usr/bin/env bash
# Full-size storage benchmarks against generated data (docs/evolution.md E6). Seeds about 1,000 conversations
# and a 200-turn conversation with 20,000-character answers in a temporary directory, then checks the read
# budgets in tests/Mindweave.Infrastructure.Tests/Performance. Measured times are printed per run.
set -euo pipefail

ROOT="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
MINDWEAVE_PERF=1 dotnet test "$ROOT/tests/Mindweave.Infrastructure.Tests" -c Release --filter "Category=Performance" --logger "console;verbosity=detailed"
