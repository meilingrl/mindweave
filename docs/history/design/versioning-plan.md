# Version numbers and the change log: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every merge into `main` raises the version's third number, and the change log has one section per version.

**Architecture:** No product code changes. The rule lives in AGENTS.md and the decision log; `Directory.Build.props` holds the number; two architecture tests keep the change log and the number in step. Tags mark the five finished phases and each new merge.

**Tech Stack:** .NET 10, xUnit (`tests/Mindweave.Architecture.Tests`), git.

**Spec:** [versioning.md](../../design/versioning.md)

## Global Constraints

- Work on the branch `task/versioning` in the worktree `../mindweave.worktrees/versioning`. Do not write in the main working directory until Task 4.
- The AGENTS.md change is a commit of its own, with no other file in it.
- Existing change-log entries are not edited. Text is only added above and between them.
- Documents are in English; short, plain sentences; terms from the glossary in architecture §2.3.
- The version form is `first.second.third`. The tag form is `v<version>`.
- The change-line verbs are exactly: Added, Changed, Fixed, Removed.
- The boundary commits are `0887946` (0.1.0), `648aac8` (0.2.0), `05b4252` (0.3.0), `6e5eb98` (0.4.0), `fbfddbd` (0.5.0).
- No tag is pushed. Pushing waits for the owner.
- `tests/Mindweave.Desktop.Tests/ReleaseChannelTests.cs` uses "0.2.0" as sample data. It is not the build's version; leave it.

## Review Focus

- **Two tasks merge on the same day and take the same number.** Expected: the second one notices. Covered by the conflict on the `<Version>` line and by the test that headings strictly descend (Task 3).
- **A merge forgets to raise the number.** Expected: it is caught before the tag. No test can see this before the merge exists; the wrap-up step in AGENTS.md owns it (Task 1). Not covered by a test.
- **`changes.md` read with CRLF line endings on Windows.** Expected: the tests pass. Covered: the tests trim each line's end (Task 3).
- **The tests run where `docs/changes.md` is absent** (the source package leaves it out). Expected: they pass without checking. Covered (Task 3).
- **A version section with a paragraph or indented sub-bullets.** Expected: allowed; only top-level bullets are checked (a milestone opens with a sentence). Covered by the test's own rule (Task 3).

---

### Task 1: The rule in AGENTS.md

**Files:**
- Modify: `AGENTS.md` (the `docs/changes.md` bullet under "Where files belong"; rule 3 and the dispatcher's "Wrap up" under "Parallel agents")

- [ ] **Step 1: Replace the `docs/changes.md` bullet** with:

```markdown
- [docs/changes.md](docs/changes.md) gets one section per merge into `main`, headed by the version and the date: one line per change, then how to try it and what was not verified ([versioning.md](docs/design/versioning.md) §5). Entries are not edited afterwards. The stage reports before 2026-11-16 are frozen in [stage-reports.md](docs/history/design/stage-reports.md).
```

- [ ] **Step 2: In rule 3**, after the sentence about the migration number, add: "The version in `Directory.Build.props` goes the same way: every merge into `main` raises its third number, and whoever merges later takes the next one. The owner calls a rise of the second number." Change the last sentence to "`STATUS.md`, `docs/changes.md` and the version are updated only when merging back into `main`; task branches do not change them."

- [ ] **Step 3: In the dispatcher's "Wrap up"**, change "update `STATUS.md` and `docs/changes.md`, push" to "raise the version, update `STATUS.md` and `docs/changes.md`, tag the merge `v<version>`, push".

- [ ] **Step 4: Check and commit**

Run: `dotnet test tests/Mindweave.Architecture.Tests --filter "FullyQualifiedName~DocsRulesTests"`
Expected: 4 passed.

```bash
git add AGENTS.md
git commit -m "Rules: the version rises with every merge; the change log is by version (owner approved 2026-11-23)"
```

### Task 2: The decision and the design's standing

**Files:**
- Modify: `docs/design/decisions.md` (append one decision)
- Modify: `docs/design/versioning.md` (first paragraph)
- Modify: `docs/design/README.md` (the `versioning.md` row)
- Modify: `Directory.Build.props:24` (the comment only)

- [ ] **Step 1: Append the decision.** Its number is the next free one when this task runs (D-036 on 2026-11-23). Heading: `## D-0NN Every merge raises the version; the change log is by version (you, 2026-11-23)`. Three bullets: the rule for the three numbers (versioning.md §3); the five phases 0.1.0–0.5.0 with their commits and that "0.2.0" builds from 2026-11-22 are not that phase; that this replaces the first sentence's rule in D-033 while D-033's Settings line stands.

- [ ] **Step 2: Reword `versioning.md`'s first paragraph** to "Approved by the owner on 2026-11-23 (D-0NN). It replaces the version rule in D-033." and the index row's opening to "In force since 2026-11-23:".

- [ ] **Step 3: Replace the comment above `<Version>`** with `<!-- The third number rises with every merge into main; the owner calls the second (docs/design/versioning.md). -->`. Leave the value.

- [ ] **Step 4: Check and commit**

Run: `dotnet test tests/Mindweave.Architecture.Tests --filter "FullyQualifiedName~DocsRulesTests"`
Expected: 4 passed.

```bash
git add docs/design/decisions.md docs/design/versioning.md docs/design/README.md Directory.Build.props
git commit -m "Decision: every merge raises the version; the change log is by version"
```

### Task 3: The changeover (tests, change log, number)

Do this task last on the branch, right after `git merge main`, because the number depends on what has merged.

**Files:**
- Create: `tests/Mindweave.Architecture.Tests/ChangeLogRulesTests.cs`
- Modify: `docs/changes.md` (intro, version table, first version section, one heading above the dated entries)
- Modify: `Directory.Build.props:25`

**Interfaces:**
- Produces: the version `0.5.N`, where N is `git rev-list --first-parent --merges --count fbfddbd..main` plus 1 (9 if nothing else has merged since 2026-11-23). Task 4 uses it for the tag.

- [ ] **Step 1: Merge `main` and compute N**

Run: `git merge main && git rev-list --first-parent --merges --count fbfddbd..main`

- [ ] **Step 2: Write the failing tests** in `ChangeLogRulesTests` (same namespace and style as `DocsRulesTests`; root from `LayeringTests.RepositoryRoot()`). Every test returns at once when `docs/changes.md` does not exist. Lines are read with their ends trimmed. A version heading matches `^## (\d+)\.(\d+)\.(\d+) · \d{4}-\d{2}-\d{2}$`; a version section runs from its heading to the next line that starts with `## `.

```csharp
[Fact] public void The_newest_version_section_is_the_build_version()
// the first version heading's version == the <Version> value in Directory.Build.props;
// fails with "docs/changes.md starts at {a} but Directory.Build.props says {b}"

[Fact] public void Version_headings_descend_and_stand_above_the_dated_entries()
// each version heading is strictly greater (numerically, part by part) than the next;
// no version heading comes after the line "## Before version headings"

[Fact] public void Version_sections_follow_the_entry_form()
// among a section's lines that start with "- " (column 0 only):
//   the last is "- Not verified: …", the one before it is "- Try: …";
//   every other one matches ^- (?:[^:]{1,40}: )?(Added|Changed|Fixed|Removed) \S  or starts with "- Documents: "
// failure lists file line numbers and the offending lines
```

- [ ] **Step 3: Run them to see them fail**

Run: `dotnet test tests/Mindweave.Architecture.Tests --filter "FullyQualifiedName~ChangeLogRulesTests"`
Expected: `The_newest_version_section_is_the_build_version` fails (no version heading yet).

- [ ] **Step 4: Set `<Version>` to `0.5.N`.**

- [ ] **Step 5: Restructure `docs/changes.md`,** top to bottom:
  1. Title, then an intro of three sentences: one section per merge into `main`, newest first, headed by version and date; the form is in versioning.md §5 and entries are not edited afterwards; where STATUS, the decision log and the frozen stage reports are (keep the existing links).
  2. `## Versions`: the table from versioning.md §4 without the commit column, plus a "Where to read" column: the stage reports for 0.1–0.3, the dated entries below for 0.4–0.5. One line under it: the merges from 2026-11-22 after 0.5.0 are 0.5.1 to 0.5.(N−1) in order and keep their dated entries.
  3. `## 0.5.N · <merge date>`: the first version section, in the new form:

```markdown
- Changed the version to rise with every merge into `main`: the third number at each merge, the second when the owner calls a milestone ([versioning.md](design/versioning.md), D-0NN)
- Changed this file to one section per version, one line per change
- Added tags for the five finished phases, `v0.1.0` to `v0.5.0`
- Added `ChangeLogRulesTests`, which keeps this file and the build's version in step
- Try: `scripts/daily.sh publish`, then read the bottom of Settings; `git tag --list 'v*'`
- Not verified: Windows. Builds from 2026-11-22 that say "0.2.0" are not the 0.2.0 of the table above.
```

  4. `## Before version headings`, with one sentence: the entries below were written by day, from 2026-11-16 to the changeover, and are kept as written. The old line "This file starts on 2026-11-22. …" moves here. The dated sections follow, demoted one level (`## 2026-11-23` becomes `### 2026-11-23`), with no other change.

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/Mindweave.Architecture.Tests`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add tests/Mindweave.Architecture.Tests/ChangeLogRulesTests.cs docs/changes.md Directory.Build.props
git commit -m "Version 0.5.N; the change log is by version, kept in step by tests"
```

### Task 4: Merge, tag, clean up

**Files:**
- Move: `docs/design/versioning-plan.md` → `docs/history/design/versioning-plan.md` (and its index rows)
- Modify: `STATUS.md` (only if it names the version rule or this task)

- [ ] **Step 1: Move this plan to history** on the task branch: `git mv`, remove its row from `docs/design/README.md`, add a row to `docs/history/design/README.md` ("Task-by-task plan for version numbers and the change log by version"). Commit.

- [ ] **Step 2: Run the full suite**

Run: `scripts/test.sh full`
Expected: the suite passes (quarantined tests only report).

- [ ] **Step 3: Check that `main` has not moved** since Task 3 step 1 (`git rev-parse main`). If it has, redo Task 3 steps 1, 4 and 5.3 with the new N and amend nothing: add a commit.

- [ ] **Step 4: Merge and tag,** in the main working directory after `git status` shows it clean and unoccupied:

```bash
git merge --no-ff task/versioning -m "Merge task: every merge raises the version; change log by version (0.5.N; owner approved 2026-11-23)"
git tag v0.5.N
git tag v0.1.0 0887946
git tag v0.2.0 648aac8
git tag v0.3.0 05b4252
git tag v0.4.0 6e5eb98
git tag v0.5.0 fbfddbd
```

- [ ] **Step 5: Verify**

Run: `git tag --list 'v*' && git describe --tags --exact-match HEAD`
Expected: six tags; `v0.5.N`.

- [ ] **Step 6: Clean up:** `git worktree remove ../mindweave.worktrees/versioning && git branch -d task/versioning`. Tell the owner the tags are local and ask before pushing.
