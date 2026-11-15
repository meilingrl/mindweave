# Engineering health check · track/claude-auto · 2026-11-12

Audited commit: `1ba95c9` (track/claude-auto, requirements v0.13 merged). Read-only: no product code, test or document outside this directory was changed.

## Verdict

The codebase is in good shape for its age and well above course-project level: 14 of 18 dimensions graded B, 4 graded C, none lower; the six follow-up areas of the gap round all graded B. No finding was rated critical, and none of the 225 findings was refuted outright by a verifier.

The weak spots are concentrated in three places:

1. **Unexpected failures have no last line of defence.** A generation run, a UI command, or start-up that throws anything outside the expected exception types either hangs the conversation until restart or closes the app. Five separate auditors reached this from different directions, and it was reproduced by tests.
2. **The project's safety net is not working.** CI has been red on every run since 2026-11-10, and the course-submission branch has 112 commits that exist only on this Mac.
3. **Nothing is shippable yet.** There is no release pipeline, no macOS package, no licence notices, and macOS — the primary platform — is not in CI.

## Act on these first

Each item below was confirmed by an independent verifier; "reproduced" means a verifier wrote a test in a throwaway worktree and ran it. Details and reproduction code are in the linked files.

| # | Problem | Evidence | Effort |
| --- | --- | --- | --- |
| 1 | **112 commits of `track/codex-steady` are not on origin** (last push 2026-10-25). All worktrees share one `.git`, so a disk failure loses the graded track's work. `main` is 3 ahead, which TRACK.md records as your decision. | Checked with `git rev-list` and `git ls-remote`. [repo-scripts](findings/repo-scripts.md) | Minutes, needs your go-ahead |
| 2 | **A crashed generation run leaves the answer "streaming" forever.** `AnswerRunner` catches only cancellation and `ChatClientException`; the client throws `InvalidOperationException` for valid JSON of an unexpected shape (for example a relay answering `{"error":"invalid api key"}`). Stop does nothing and new questions in that conversation are refused until restart. The title, summary and naming runners share the pattern. | Reproduced end to end with real stores. [concurrency](findings/concurrency.md), [ai-client](findings/ai-client.md), [maintainability](findings/maintainability.md) | S |
| 3 | **An uncaught exception in an async UI command closes the app.** There is no dispatcher-level handler. Renaming a conversation, or saving settings, while storage is paused does this — and the kept, unsaved answer is lost with it. | Reproduced. [errors](findings/errors.md), [ui-vm](findings/ui-vm.md) | S for the handler, M for the individual paths |
| 4 | **Start-up has no failure path once the database opens.** A read-only database file opens as "ready", then the first recovery write throws and every launch crashes with no window. | Reproduced. [errors](findings/errors.md), [concurrency](findings/concurrency.md) | S |
| 5 | **Answers cut off by the provider are stored and shown as complete.** `finish_reason=length` and `content_filter` leave no visible sign; an empty truncated answer is saved as a complete empty answer, and the cut-off text is sent back as context. This also misses a must-level requirement. | Reproduced. [ai-client](findings/ai-client.md), [requirements](findings/requirements.md) | M |
| 6 | **Model output can make the app fetch any URL.** Markdown images in an answer are loaded without user action, during streaming and from stored answers, so an answer can send conversation text to a remote server in the URL. `file://` images are opened too. | Reproduced against a loopback listener. [security](findings/security.md) | S–M |
| 7 | **CI has been red for 12 runs in a row, including HEAD.** The failing test is the font-dependent `SourceHighlightThemeTests` pixel check on Ubuntu; two runs failed earlier at the font install step. Six feature merges landed on red. | Checked with `gh run list`. [ci-build](findings/ci-build.md), [cross-platform](findings/cross-platform.md) | S |
| 8 | **Compression and summary requests always send `max_tokens=16384`.** Models whose output limit is lower reject them, so any conversation long enough to need compression fails, and so does "summarise". TRACK.md records the small-window case as pending; this is broader. | Reproduced with a simulated provider limit, not against a real service. [ai-client](findings/ai-client.md) | M |
| 9 | **Two pages of the same conversation in the back stack each keep their own draft.** Going back can show an empty box and then overwrite the newer draft. | Reproduced. [ui-vm](findings/ui-vm.md) | M |
| 10 | **Saving a summary edit with an empty title or body closes the app.** The write hits a CHECK constraint that nothing catches, and the empty draft survives the restart. A concrete case of item 3, found in the gap round. | Reproduced. [gap-summary-and-network-subsystem](findings/gap-summary-and-network-subsystem.md) | S |
| 11 | **In-window dialogs are not modal for the keyboard.** Focus stays behind the scrim; Tab leaves the dialog; Enter and Esc do not reach it in three dialogs. | Reproduced with real key input. [a11y-i18n](findings/a11y-i18n.md) | M |

## Next tier

Confirmed at medium severity; worth scheduling.

- **A restored database leaves WAL mode for good.** WAL is set only when a database is created, and `VACUUM INTO` backups are rollback-journal files. After any restore, readers and the writer block each other. [storage](findings/storage.md)
- **Deletes slow down with total history.** Three foreign keys into `message` have no index. At the project's budgeted size this is not noticeable; at 102k messages a 1,000-message workspace delete took 16.9 s, and 9 ms with the indexes. Needs a migration. [performance](findings/performance.md)
- **The message list is not virtualised.** Opening a conversation costs time in proportion to its total text (about 1 s at 150k characters on macOS). [performance](findings/performance.md)
- **Open errors are reported as a damaged database** with advice to restore from backup, including lock and permission errors. [errors](findings/errors.md)
- **Stream handling gaps:** the 120 s idle timeout ignores keep-alive comments, and an HTTP 200 with a non-streaming body is reported as a generic failure. [ai-client](findings/ai-client.md)
- **Settings and workspace pages show stale state:** app-wide settings are loaded once per workspace page; the Workspaces page never recounts. [ui-vm](findings/ui-vm.md)
- **"Mark unread" on a summary is undone** by the next reading-progress report. [requirements](findings/requirements.md)
- **Failures are hard to diagnose:** the desktop layer has 26 bare `catch (Exception)` blocks and one logger; failed page loads are silent; crashes and failed launches show the user nothing. [observability](findings/observability.md)
- **Release engineering:** no release pipeline, no macOS bundle or signing, version fixed at 0.1.0 with the commit not stamped into the build; published output carries no third-party licence notices. [ci-build](findings/ci-build.md), [dependencies](findings/dependencies.md)
- **Test gaps:** no test for a stream that ends without a completion event; client timeouts untested; `FakeChatClient` does not reproduce real stream shapes. [tests](findings/tests.md)
- **TRACK.md is behind HEAD:** "next step" still says to draft the requirements update that is already merged as v0.13, and the status section is stacked history. [docs](findings/docs.md)

## Scorecard

Counts are after verification, for the 18 main dimensions. Auditors' severities were often lowered: 29 findings started as high and 18 remain; 76 started as medium and 27 remain. With the gap round the totals are 19 high, 35 medium and 171 low or info.

| Dimension | Grade | High | Medium | Low/info |
| --- | --- | --- | --- | --- |
| [Data integrity and SQLite storage](findings/storage.md) | B | | 2 | 7 |
| [Concurrency, async and lifecycle](findings/concurrency.md) | B | 1 | 1 | 8 |
| [Error handling and resilience](findings/errors.md) | B | 3 | 3 | 3 |
| [Security and privacy](findings/security.md) | B | 1 | | 6 |
| [AI provider client and context management](findings/ai-client.md) | B | 3 | 2 | 5 |
| [Architecture conformance](findings/architecture.md) | B | | | 9 |
| [Requirements conformance](findings/requirements.md) | B | 1 | 2 | 6 |
| [Test suite quality](findings/tests.md) | B | | 2 | 8 |
| [Desktop UI and ViewModel correctness](findings/ui-vm.md) | B | 2 | 2 | 6 |
| [Performance and resource use](findings/performance.md) | C | 1 | 1 | 8 |
| [Build, CI and release engineering](findings/ci-build.md) | C | 1 | 2 | 7 |
| [Dependencies and supply chain](findings/dependencies.md) | C | | 1 | 7 |
| [Code quality and maintainability](findings/maintainability.md) | B | 2 | 1 | 6 |
| [Documentation health](findings/docs.md) | C | | 1 | 9 |
| [Accessibility, UX robustness and i18n readiness](findings/a11y-i18n.md) | B | 1 | 3 | 6 |
| [Cross-platform correctness](findings/cross-platform.md) | B | | 1 | 9 |
| [Observability and diagnostics](findings/observability.md) | C | 1 | 3 | 6 |
| [Repository hygiene and scripts](findings/repo-scripts.md) | B | 1 | | 9 |

The 18 high rows are 11 distinct problems: the ten main-round items above plus the missing foreign-key indexes, which one verifier kept at high and the reproduction lowered to medium. Several auditors found the same defect independently; items 2 and 3 account for 8 of the 18.

## What is healthy

- **Storage core.** One serialised writer with `BEGIN IMMEDIATE` transactions, single-snapshot reads, STRICT tables, single-transaction migrations with `foreign_key_check`, verified `VACUUM INTO` backups, and real fault-injection tests (kill before commit, disk full, busy writer). The storage auditor found no path that loses committed data in normal operation.
- **Layering.** The architecture auditor found no medium-or-higher violation; every architecture finding ended at low.
- **Secrets.** Keys live in Keychain or Credential Manager and unsupported platforms refuse rather than fall back to plaintext; redirects are disabled so a key cannot follow one; SQL is parameterised throughout; log templates carry IDs and counts, never message text. No vulnerable package is reported, including transitive ones.
- **Baseline.** Build has 0 warnings with warnings as errors. Full suite on macOS: 892 passed, 76 skipped, 0 failed (Core 91, Architecture 8, Infrastructure 412 + 18 skipped, Desktop 381 + 58 skipped).

## Baseline numbers

| Check | Result |
| --- | --- |
| `dotnet build Mindweave.slnx` | 0 warnings, 0 errors |
| `dotnet test Mindweave.slnx` (macOS) | 892 passed, 76 skipped, 0 failed |
| Vulnerable packages (with transitive) | none |
| Outdated packages | Avalonia 12.1.4 → 12.1.6; LiveMarkdown 2.5.1 → 2.6.1; xunit.v3 3.2.2 → 4.0.3; Microsoft.NET.Test.Sdk 18.11.0 → 18.12.0 |
| `dotnet format --verify-no-changes` | fails: 131 whitespace errors (127 in `tests/Mindweave.TestHost/Program.cs`, 4 in `src/Mindweave.Application/Models/ModelSettingsService.cs`); no `.editorconfig`, not checked in CI |
| CI on `track/claude-auto` | red since 2026-11-10; last green `8810ea2` |

## How this was done

- **Audit.** 18 read-only auditors, one per dimension, each limited to 10 findings and told to skip what TRACK.md already records.
- **Verification.** Every medium-or-higher finding went to a skeptic told to refute it by reading the code. High findings also went to a second agent that tried to reproduce them with a test in an isolated worktree, or by direct inspection for process findings.
- **Own checks.** I re-checked items 1, 2, 3 and 7 directly (git, `gh`, and reading `AnswerRunner.cs` and `Program.cs`) and ran the baseline myself.
- **Scale.** 181 agents, about 20 million tokens, in two runs (the first stopped at the usage limit during the gap round).

## Limits

- **Low and info findings (171 after verification, 101 of them never sent to a verifier) are unverified auditor claims** or findings a verifier lowered. Treat them as leads.
- **Nothing ran on Windows.** All reproductions are macOS; Windows-specific claims rest on code reading.
- **No real AI service was called.** Provider-behaviour claims (item 8, default output limits) use simulated responses.
- **Reproduction tests were deleted** after running; their code is quoted in the findings files. Passages first cut for length were restored in full from the review's run record on 2026-11-15.
- **Counts include duplicates.** The same defect reported by several auditors is counted once per auditor.

## Gap round

A completeness critic named six areas the 18 dimensions covered thinly. Each got its own auditor and the same verification. All six graded B.

| Area | High | Medium | Low/info |
| --- | --- | --- | --- |
| [Branch from a selection and return to source](findings/gap-branch-anchor-roundtrip.md) | | 1 | 8 |
| [Summary lifecycle and network persistence](findings/gap-summary-and-network-subsystem.md) | 1 | 1 | 8 |
| [Archive, delete, exit and recovery journeys](findings/gap-lifecycle-journey-archive-delete-exit-recovery.md) | | 2 | 6 |
| [Background AI requests and statistics accuracy](findings/gap-unprompted-ai-spend-and-statistics-accuracy.md) | | 2 | 8 |
| [Upgrade path and stored settings](findings/gap-schema-upgrade-path-and-settings-state.md) | | 2 | 6 |
| [Native interop and OS integration](findings/gap-native-interop-and-process-boundary.md) | | | 10 |

Confirmed at medium or higher:

- **Empty summary title or body closes the app** — item 10 above.
- **Usage is never requested from the service** (`stream_options` is not sent), so statistics and token calibration stay empty for endpoints that follow OpenAI's behaviour. TRACK.md notes the official API was never tested; this confirms the gap in the code.
- **Inline `<think>…</think>` text is not stripped** from outputs the program uses directly, such as titles and names.
- **Adopting a candidate or switching the current version marks it read** within about 300 ms, using the previous version's reading position.
- **A branch quote can substitute the wrong text for a formula** when the paragraph contains another `$` or an embedded object.
- **Summary, naming and title tasks do not keep their final write when storage refuses it**; the task row stays queued or running until restart.
- **Every OS logout, restart or shutdown is vetoed**, even when nothing is unsaved.
- **A schema version is only an integer.** An edited or renumbered migration cannot be detected, and upgrade tests build their "old" databases from today's migration code rather than from saved historical files.
- **After a restore, model settings can point at a key already deleted from the OS store**, and the settings page still says a key is saved.

The anchoring core held up: the auditor probed nested lists, tables, code, CRLF text and emoji and could not break capture or relocation. The hand-written credential-store interop was read as correct, with all ten findings ending at low.
