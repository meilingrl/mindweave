# Test-code maintainability review: tests/ (Mindweave, branch track/claude-auto, read-only)

## Verdict
- **Size is not the main problem.** There are 633 test methods averaging 21 lines. Only 5 are over 60 lines and 37 are over 40.
- **Most large files are cohesive.** GalleryTests, NetworkCanvasTests, SidebarStatusTests, TransactionKillTests and MultiSelectUiTests each cover one feature or one fault model. That is fine.
- **The real problems:**
  - About 85 private helper definitions are copied across files (roughly 550–650 lines, about 3% of the tests), and some copies have quietly drifted apart.
  - Test classes are used as helper libraries by other test classes.
  - One harness (`ChatHarness`) wires services by hand, a second copy of the app's service wiring (`CompositionRoot`).
  - `FakeChatClient` has knobs that are inconsistent and confusingly named.
  - A few files mix unrelated features (ExitTests, ConversationActionsTests).

## Evidence: duplicated helpers

Counts come from grepping `private/internal static` definitions.

| Helper | Copies | Where | Notes |
|---|---|---|---|
| `Find<T>(root,name)` | 9 | ConversationPolish 464, SettingsPolish 461, View 89, MultiSelectUi 539, StoragePauseUi 160, ArchiveUi 479, WorkspaceDeleteUi 293, BranchUi 343, Gallery 1186 | Lookup semantics differ: `.First` in MultiSelectUi vs `.Single` in ViewTests |
| `Click` | 8 | ConversationActions 511, ConversationPolish 424, SidebarLayout 232, SettingsPolish 459, SidebarStatus 609 forward to `UiInput.Click` | **Drifted:** MultiSelectUiTests:529 and BranchMarksTests:454 press the mouse directly and skip `UiInput.Click`'s settling and pressed-check, which is the fix for the 2026-11-05 Windows flake (TRACK.md 发现). ViewTests:81 `Click` doesn't click at all: it runs `Command.Execute` |
| `Open(app)` / `Show(app)` (`new MainWindow{…}.Show()`) | 7 + 2, and 29 `new MainWindow` sites | Sizes vary: 1000×700 or 1100×720 | — |
| `CurrentView(window)` | 7 | Byte-identical one-liners | — |
| Desktop `ConversationAsync(app)` | 5 | ExitTests:592 (internal), ConversationPolish 444, ArchiveUi 447, SummaryUi 371, ConversationActions 533 | **Drifted:** ArchiveUi and ConversationPolish also wait for the title; the others don't |
| Desktop `AskAsync(app, held)` | 3 | StreamingIndicator 114, AnswerTimer 221 (identical); StreamingSelection 215 (variant) | 11 desktop `AskAsync` definitions in all, with 6 different meanings |
| Infra `AskAsync(h, conv, q)` | 5 | TransactionKill 456, BranchFlow 171, WorkspaceDeleteFlow 438, ArchiveFlow 416, ContextCompression 273 | **Byte-identical** (same md5); 2 more variants |
| Infra `BranchAsync` / `SummarizeAsync` / `CountAsync` / `RetryAsync` / `RootWithAnswerAsync` | 4 / 5 / 5 / 3 / 3 | Infrastructure Chat and Faults tests | `CountAsync(h, table)` is identical in ArchiveFlow and BranchFlow |
| Local `UntilAsync` | 3 | ConversationDeleteFlow 400, SummaryFlow 311, ArchiveFlow 431 | Plus `ChatHarness.UntilAsync`: 75 calls in 23 files, and its timeout message doesn't say which condition failed (`UiWait` does) |
| `Colour` / `Scroller` / `Part` | 4 / 3 / 2 | — | — |
| `StubbornClient` | 2 | ExitStopTests:236, Desktop ExitTests:688 | Identical except for the attribute namespace |
| `FailingDrafts` | 2 | ExitTests:622, GalleryTests:1298 | Identical |
| Fake clocks (`TimeProvider`/`IClock`) | 9 | SteppingTime, FixedTime, StepClock, LaterTime, ManualTime ×2, SetTime, GalleryTime, GalleryClock | — |
| `Ct => TestContext.Current.CancellationToken` | 42 files | — | Desktop tests also pass `CancellationToken.None` 150 times |

### Test classes used as helper libraries by other test classes
- `NetworkUiTests.Show` / `OpenNetworkAsync` / `SeedAsync` / `BranchAsync` / `AskAsync` / `MoveSomethingAsync`: about 150 calls from 8 other files (Show alone 63).
- `ConversationFlowTests.FlakyStore`: 14 uses in ConversationMarks, StorageHealth and ExitStop tests.
- `ViewTests.Capture`: 10 uses.
- `ExitTests.FailingSummaryDrafts` and `ExitTests.ConversationAsync`: used by DraftLossTests.

Changing a test class this way silently breaks unrelated suites.

### Timing patterns
- **About 25 fixed `Task.Delay`s.** Almost all are "nothing happens" checks: wait, then assert a state still holds. Examples: ConversationFlowTests:109 and 145, StorageRecoveryTests:151, ExitStopTests:178, StorageHealthTests:65, DiskFullTests:65 and 139, BusyWriterTests:74, ExitTests:307, Conversation/WorkspaceDeleteFlowTests:321/231. These are legitimate, but the intent is implicit and they cost about 4 seconds in total.
- **One fixed delay waits for something to happen:** `BranchUiTests:315` sleeps 400 ms for scrolling to settle. This is fragile under load, the same class of problem as TRACK.md 发现 2026-11-05.
- **30 `UiWait.UntilAsync(() => …Async(…).Result)`** (for example NetworkUiTests:246). These block on an async storage read while pumping the UI thread.
- **Hand-rolled polling loops:** StreamingSelectionTests:161, SidebarStatusTests:492, GalleryTests:1204.
- **Waits on stored state are already correct.** `CanBranch` requires `_live is null`, so the 15 `CanBranch` waits do wait for the stored row. They don't need changing.

## Findings and proposals (highest priority first)

### 1. Shared desktop UI helpers — priority high, effort S
**Problem:** the 7–9 copies of Find/Click/Open/CurrentView/Part/Scroller, and the two `Click` copies that skip `UiInput`'s protection.

**Proposal:** add `tests/Mindweave.Desktop.Tests/Support/`:
- `UiTree` (static):
  - `Find<T>(Visual, name)`, always `.Single`
  - `Part<T>`
  - `CurrentView(Window)`, `Scroller(Window)`, `Answers(Window)`, `Canvas(Window)`
  - `ThemeColour(key, theme)`
- `DesktopHarness.ShowWindow(double width = 1000, double height = 700)`, replacing `Open`/`Show` and the 29 `new MainWindow` sites.
- `UiInput`: every click goes through it. Rename `ViewTests.Click` to `Invoke`, because it runs the command directly instead of clicking.

**Migration:**
1. Add the helpers.
2. Replace the identical copies one file per commit: CurrentView, Find, Open.
3. Move MultiSelectUiTests and BranchMarksTests to `UiInput.Click` last, and run them in a busy loop (TRACK's 10-busy-loop method).

**What protects it:** the full Desktop suite with an unchanged pass/skip count (668/59), plus one full run with the 10 ms sync delay.

### 2. Take shared helpers out of test classes — priority high, effort S
**Proposal:**
- `Support/NetworkScenario.cs`: `SeedAsync`, `OpenNetworkAsync`, service-level `AskAsync`/`BranchAsync`, `MoveSomethingAsync`.
- `Support/ConversationScenario.cs`, as extension methods on `DesktopHarness`:
  - `StartWithConversationAsync(question, waitForTitle: bool)`: one implementation of the 5 `ConversationAsync` copies, with the title wait made an explicit parameter.
  - `AskHeldAsync(held)`.
  - `OpenConversationAsync(id)`.
- `Support/Screenshots.Capture`, moved from ViewTests.
- Store fakes into `Support/Stores/`: `FlakyStore`, `FailingDrafts`, `FailingSummaryDrafts`.
- `StubbornClient` into `Mindweave.Testing`. It only needs Application, which that project already references.

**Migration:** move one type at a time; the compiler finds every caller. The Network*, Exit, DraftLoss, ConversationMarks, StorageHealth and ExitStop suites protect it.

### 3. Infrastructure flow helpers — priority medium, effort S
**Proposal:**
- A `ChatFlow` static class of extensions on `ChatHarness`:
  - `AskAsync`, `RootWithAnswerAsync`, `BranchAsync(parent, answer, quote = default)`, `RetryAsync`
  - `SummarizeAsync`, `CountAsync(table)`
- Give `ChatHarness.UntilAsync` the same `[CallerArgumentExpression]`/file/line diagnostics `UiWait` has, plus a `Func<Task<bool>>` overload. Delete the 3 local copies.

**Migration:** replace the identical `AskAsync` copies first, then the variants with their extra parameters. The Chat and Faults suites protect it.

### 4. `FakeChatClient` knobs — priority medium, effort M
**Problem:**
- Four request kinds each have their own, inconsistent knobs:
  - context summary: `SummaryGate`, `FailSummaries`, `TruncateSummaries`
  - knowledge summary: `KnowledgeGate`, `TruncateKnowledge`, plus `SummaryReplies`, whose name suggests context summaries but which actually feeds knowledge summaries
  - naming: `NamingGate`, `NamingReplies`
  - title: `HoldTitles()` method, `TruncateTitles`
- 7 of these knobs are used in only one file.
- `StreamAsync` is one 130-line method that branches by kind.

**Proposal:**
- One `RequestScript` object per kind, exposing `Gate`, `FailNext(kind)`, `TruncateNext(n)` and `Replies`.
- Properties `client.Titles`, `client.ContextSummaries`, `client.Knowledge`, `client.Naming`. Answers keep `Hold`, `HoldThinking`, `Reply`, `Fail`, `Push`.
- `StreamAsync` becomes: classify the request, then `script.StreamAsync`.
- Optionally absorb `StubbornClient` as `Hold(...).IgnoringCancellation()`.

**Migration:**
1. Add the new API and turn the old properties into `[Obsolete]` forwarders.
2. Move callers one kind at a time; `SummaryReplies` (17 uses in 9 files) and `KnowledgeGate` (16 uses in 5 files) are the biggest.
3. Remove the forwarders.

**What protects it:** the Summary*, ContextCompression, Naming and title tests in Archive/ExitStop, and ThinkingPhaseTests.

### 5. `ChatHarness` duplicates the service wiring — priority medium, effort M
**Problem:**
- The constructor (lines 21–58) creates about 20 services by hand, alongside `CompositionRoot` (56 registrations).
- 13 of the last 13 commits that touched ChatHarness also changed src. In the larger ones, every constructor change was made twice.
- Splitting `GenerationCoordinator` will hit this file too.

**Proposal:**
- Move the application-service registrations out of `CompositionRoot` into an `AddMindweaveApplication()` extension, and have `CompositionRoot` call it. This is a production change, so it needs a check against the architecture tests.
- Build `ChatHarness` from `AddMindweaveInfrastructure` + `AddMindweaveApplication`, with test overrides for the chat client, credentials, the timing options and the `wrapStore` decorator.
- Keep the typed properties as `GetRequiredService` lookups.

**Order:** do this **before or together with** the GenerationCoordinator split. All Infrastructure tests plus the Architecture tests protect it.

### 6. Split ExitTests (729 lines, 4 features) — priority medium, effort S
| Part | Lines | New file |
|---|---|---|
| Exit prompts: layout and task prompts, cancel | 37–143 | ExitPromptTests |
| Saving before exit: drafts, failed save and retry, summary drafts, export finishing | 145–334, 484–513 | ExitSavingTests |
| Host lifecycle: throwing steps, second launch, bounded stop wait, database closes after the UI loop, single-instance lock, disposal failure, `PumpUntil` | 336–555 | AppHostLifecycleTests |
| Fakes | 606–729 | Moved to Support as in item 2; `Quit`, `DirtyLayoutAsync` and `RunningAnswerAsync` go to an `ExitScenario` helper |

Pure moves, so the same tests run; check that the test count is unchanged.

### 7. GalleryTests (1319 lines) — fine as a catalog; priority low, effort S
- It is a list of 40 independent scenes, and the most-churned test file (19 commits), because each UI feature adds a scene. Don't split the scenes.
- **Do move out:**
  - content constants (43–110) into `GalleryTests.Content.cs`
  - fixture code (1035–1319: `Seed`, seeding helpers, `Scene`, `GalleryTime`/`GalleryClock`, the duplicate `FailingDrafts`) into `GalleryTests.Fixture.cs` as a partial class, reusing items 1–2
- **Check:** `--filter Category=Gallery` with `MINDWEAVE_SCREENSHOT_DIR` set; frames should be unchanged.

### 8. One fake clock — priority low, effort S
- Add `Mindweave.Testing/ManualTime : TimeProvider, IClock` (start time, `Advance`, optional real-time drift as in `GalleryTime`) and replace the 9 variants.
- Alternatively use the `Microsoft.Extensions.TimeProvider.Testing` package, which isn't referenced today; `IClock` would still need a small adapter.

### 9. Make timing intent explicit — priority low, effort S
- Add a `Stays(condition, duration)` helper next to `UntilAsync`. It checks repeatedly and fails as soon as the condition breaks. Use it in the ~20 "nothing happens" delays.
- Replace the 400 ms sleep at `BranchUiTests:315` with a wait until the scroll offset is stable.
- Add an async-condition overload to `UiWait` and remove the 30 `.Result` calls.
- Replace the 3 hand-rolled polling loops with the helpers.
- **Check:** busy-loop runs plus the 10 ms sync-delay run, as recorded in TRACK.md.

### 10. Long tests that check many separate things — priority low, effort S
- `SummaryUiTests:281` (74 lines) checks six behaviours: history hidden while editing, row labels, switching the shown version, badge outline, delete enabling, and the danger-tone confirm. Split it into 3 tests.
- Optionally also: `StoragePauseUiTests:29` (77 lines), `SummaryUiTests:212`, `MultiSelectUiTests:81` and `:141`.
- Everything else is reasonable.

### 11. Files that mix features — priority low, effort S
- **ConversationActionsTests:** the floating branch action (53–225, 335–377, helpers 408–503) is mixed with message times, copy and stop (37–52, 226–334, 378–405).
  - Move the second group to `MessageActionsTests`.
  - The `.LateReload.cs` partial is the precedent for this kind of split.
- **SqliteDatabaseTests (522 lines):** four clean sections — opening and file classification (18–207), migration and backups (68–279), write queue and draining (280–389), process kill (390–457). Optional split: `SqliteMigrationTests` and `SqliteWriteQueueTests`.
- **Leave as they are:**
  - **SidebarStatusTests:** one feature (sidebar marks), even if the file is long.
  - **NetworkCanvasTests:** one feature (canvas drawing), even if the file is long.
  - **TransactionKillTests:** one fault model applied to 9 operations.
  - **ArchiveFlowTests:** its delete tests are about deleting archived items.
  - **MultiSelectUiTests:** one feature (multi-select).

## Suggested order
1. Items 1, 2 and 3: shared helpers, about 600 duplicated lines removed. These don't affect each other and can run in parallel worktrees.
2. Item 5 (ChatHarness wiring), before or together with the GenerationCoordinator refactor.
3. Item 4 (FakeChatClient).
4. Items 6 and 7 (file splits).
5. Items 8–11 opportunistically.

Every step changes only test code, except item 5. For each step, the full suite must pass with an unchanged pass/skip count; steps that touch timing (1, 9) also get a busy-loop run and the 10 ms sync-delay run.