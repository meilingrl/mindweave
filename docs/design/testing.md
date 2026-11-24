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
- **CI gives each desktop shard a machine of its own.** Each system runs four jobs at once: three desktop shards (`test-shards.py run … --shards 3 --only I`) and one job for Core, Architecture, Infrastructure, the package check and the quarantined tests. Every job restores and builds for itself. The same commit lists the same classes and reads the same timings in every job of one system, so the three shards together run every class once. On 2026-11-23 (895 desktop tests) one run took 6 min 30 s, against 12–15 min for the single job before it; the Windows desktop shards took 4.0–4.5 min each, and the slowest job was Windows "rest" (Infrastructure, 3 min 37 s of tests). The number of shards is the `part` list and `DESKTOP_SHARDS` in `ci.yml`; more desktop shards gain nothing until the "rest" job is shorter.
- Shards inside one CI job do not pay: on 2026-11-16, 2 shards in the one job (522 desktop tests) took Linux desktop from 4 min 10 s to 3 min 30 s and Windows from 7 min 44 s to 6 min 48 s, the whole step barely changed because Infrastructure then ran after the desktop tests and not beside them, and Windows had one extra double-click timing failure.
- Classes are listed at run time, so newly added classes are never skipped; `tests/Mindweave.Desktop.Tests/shard-timings.json` is used only to balance the shards (classes without a record count as the median). Outdated records only make the shards uneven; they never cause tests to be missed. `timings` refreshes them.
- The test scripts write their reports as UTF-8 when their output is redirected, and read the runner's output as UTF-8. Test names and failure messages hold Chinese, and on Windows a redirected Python otherwise uses the ANSI code page: on 2026-11-24 a failing shard on Windows CI stopped with `UnicodeEncodeError` while printing its failure, and the message was lost. `scripts/script-checks.py` runs both scripts with that code page forced and Chinese in every name and message; `suite` and CI run it.
- Sharding requires that tests share no resources outside the process: each test uses its own data directory, and the single-instance lock is also keyed by data directory. A new test that uses a fixed file path, pipe name or port will interfere with others when sharded, and must be changed to be per test.
- When debugging a single desktop test locally, still use `quick NAME` or `dotnet test --filter`, which do not go through sharding.

## Time budget

- A single test over 3 seconds (except in the `Faults` and `Performance` namespaces) is recorded as a warning on CI, not a failure: runner speeds vary too much, and failing on time would itself be intermittent.
- A new test over budget must have a reason (a real child process, a real lock wait, and so on); where `ManualTime` can advance time, do not use real waits.
- Data rules go in Infrastructure tests (fast, can run in parallel); desktop tests only verify what is seen in the UI and how it is wired, and do not verify storage behavior again.
- A new page, form or menu gets a scene in `CutOffPagesTests`, which checks in both languages, at the first and at the smallest window size, that nothing is cut off and no controls lie over each other.

## Intermittent failures

- No automatic retries: retries only hide the problem.
- As soon as an intermittent failure is found (on CI or locally), record it in `STATUS.md`. If the cause can be found on the spot, fix it; if not, add `[Quarantined("date", "reason")]` (`Mindweave.Testing`). Quarantined tests still run on CI, but in a separate step that does not block, so a red CI only means a real problem.
- Quarantine is temporary: the next fix batch fixes these tests first, and the attribute is removed once they are fixed. The standard for fixed is `scripts/test.sh repeat 30 … --load` with no failures.
- Common causes and the matching fixes:
  - A wait sets a deadline on the real clock (`UiWait` defaults to 5 seconds) and a slow machine cannot make it → wait for the actual event or state, and widen the deadline if needed;
  - A real timer (pulse, toast auto-dismiss) ends before the check → use `ManualTime` instead, or count starts instead of looking at the momentary state;
  - Layout measurements depend on the font (the runner's fallback font is wider) → assert relationships (no overlap, inside the track) instead of pixel values, or leave a margin for font differences.
