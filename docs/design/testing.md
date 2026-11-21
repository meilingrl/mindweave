# Test organization and health

Settled after the test health check on 2026-11-15 (at the time 1,157 tests, about 106 seconds for the full suite locally). This file covers only how this track's tests are run and what to do when they get slow or unstable; what each stage tests is in that stage's design.

## How to run

| Command | What it runs | When to use it |
| --- | --- | --- |
| `scripts/test.sh quick [NAME…]` | Core, Architecture, Infrastructure (without fault injection), plus the desktop tests whose full name contains any of the names | The loop while changing code, about 15–30 seconds |
| `scripts/test.sh suite [DIR]` | The tests that block CI, with desktop tests run in parallel shards (see below); writes TRX when given a directory | Full suite locally |
| `scripts/test.sh full` | `suite`, then the quarantined tests run separately, report only | Before merging back into the track branch; must pass |
| `scripts/test.sh repeat N FILTER [PROJECT] [--load]` | Runs the same filter N times in a row and counts failures; `--load` also keeps all cores busy | Hunting intermittent failures |
| `scripts/test.sh timings [DIR]` | Full suite with TRX recorded; lists the slowest projects, classes and tests, and the tests over budget; also refreshes the per-class timings used for sharding | When things feel slower; after desktop test classes are noticeably added or removed |
| `scripts/test.sh quarantined` | Lists the quarantined tests | At the start of a fix batch |

Existing categories (the `Category` attribute): `FaultInjection`, `Performance`, `Gallery`, `RealApi`, `Trial`, `Quarantine`. By default, depending on environment variables or attributes, the last four do not run or do not block.

## Desktop test shards

Avalonia headless tests have only one UI thread per process, so desktop tests in the same process can only run one after another; projects such as Infrastructure run in parallel within the process. So `suite` splits the desktop test assembly into several shards by class and runs several processes at once (`scripts/test-shards.py`). On 2026-11-16 locally (10 cores): desktop tests went from about 100 seconds to about 33 seconds, with no failures in consecutive runs with 4, 6 and 8 shards; beyond 4 shards the gain is small, and the floor is the slowest class plus process startup.

- The number of shards defaults to half the number of cores, between 2 and 4; `MINDWEAVE_TEST_SHARDS` can change it.
- **CI does not shard**; it is still `dotnet test Mindweave.slnx`. On 2026-11-16, 2 shards were tried on CI (the same 522 desktop tests): Linux desktop 4 min 10 s → 3 min 30 s, Windows 7 min 44 s → 6 min 48 s. But the original command already ran Infrastructure and desktop at the same time; with sharding the two run one after the other, so the whole step's time barely changed, and Windows had one extra double-click timing failure. The standard runners for private repositories have few cores; sharding only pays off on larger runners.
- Classes are listed at run time, so newly added classes are never skipped; `tests/Mindweave.Desktop.Tests/shard-timings.json` is used only to balance the shards (classes without a record count as the median). Outdated records only make the shards uneven; they never cause tests to be missed. `timings` refreshes them.
- Sharding requires that tests share no resources outside the process: each test uses its own data directory, and the single-instance lock is also keyed by data directory. A new test that uses a fixed file path, pipe name or port will interfere with others when sharded, and must be changed to be per test.
- When debugging a single desktop test locally, still use `quick NAME` or `dotnet test --filter`, which do not go through sharding.

## Time budget

- A single test over 3 seconds (except in the `Faults` and `Performance` namespaces) is recorded as a warning on CI, not a failure: runner speeds vary too much, and failing on time would itself be intermittent.
- A new test over budget must have a reason (a real child process, a real lock wait, and so on); where `ManualTime` can advance time, do not use real waits.
- Data rules go in Infrastructure tests (fast, can run in parallel); desktop tests only verify what is seen in the UI and how it is wired, and do not verify storage behavior again.

## Intermittent failures

- No automatic retries: retries only hide the problem.
- As soon as an intermittent failure is found (on CI or locally), record it in `STATUS.md`. If the cause can be found on the spot, fix it; if not, add `[Quarantined("date", "reason")]` (`Mindweave.Testing`). Quarantined tests still run on CI, but in a separate step that does not block, so a red CI only means a real problem.
- Quarantine is temporary: the next fix batch fixes these tests first, and the attribute is removed once they are fixed. The standard for fixed is `scripts/test.sh repeat 30 … --load` with no failures.
- Common causes and the matching fixes:
  - A wait sets a deadline on the real clock (`UiWait` defaults to 5 seconds) and a slow machine cannot make it → wait for the actual event or state, and widen the deadline if needed;
  - A real timer (pulse, toast auto-dismiss) ends before the check → use `ManualTime` instead, or count starts instead of looking at the momentary state;
  - Layout measurements depend on the font (the runner's fallback font is wider) → assert relationships (no overlap, inside the track) instead of pixel values, or leave a margin for font differences.
