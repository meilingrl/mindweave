#!/usr/bin/env bash
# Full-size benchmarks (docs/evolution.md E6), each with its own budgets; measured times are printed per run.
# Storage: seeds about 1,000 conversations and a 200-turn conversation with 20,000-character answers in a temporary
# directory, then checks the read budgets in tests/Mindweave.Infrastructure.Tests/Performance.
# Rendering: shows, opens and streams answers of about 30,000 and 100,000 characters of Markdown in the headless
# window with real text layout (tests/Mindweave.Desktop.Tests/Performance; docs/history/design/s2-branching.md "性能").
set -euo pipefail

ROOT="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
MINDWEAVE_PERF=1 dotnet test "$ROOT/tests/Mindweave.Infrastructure.Tests" -c Release --filter "Category=Performance" --logger "console;verbosity=detailed"
MINDWEAVE_PERF=1 dotnet test "$ROOT/tests/Mindweave.Desktop.Tests" -c Release --filter "Category=Performance" --logger "console;verbosity=detailed"
