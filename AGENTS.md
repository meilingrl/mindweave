# Mindweave repository rules

What the project is and which documents to read first: see the [README](README.md). This file covers branches, the division of work between people and agents, and the conventions for several agents working in parallel.

## Branches

There is one line of development: `main`, with the local working directory `Projects/mindweave/`. Requirements, architecture, design, code and tests all live here. Each task is developed on a short-lived task branch (see "Parallel agents") and merged back into `main` after the full test suite passes.

Before starting work, run `git branch --show-current` to confirm the branch, then read [STATUS.md](STATUS.md) at the repository root.

**History.** From 2026-10-25 to 2026-11-16 two tracks ran in parallel: autonomous development by Claude, and the user working with Codex. Actual development showed that software this interaction-heavy cannot do without a person trying it and giving feedback, so the distinction between the tracks stopped making sense and they were merged into one (user's decision, 2026-11-16).

- All work of the Claude track is in `main`; its progress record from that time is [docs/history/track-claude-auto.md](docs/history/track-claude-auto.md).
- The Codex track stopped at the tag `archive/codex-steady-final`: read-only and not to be merged. To consult it, use `git show archive/codex-steady-final:<path>`; its conclusions must be verified independently, and borrowing its code requires the user's decision.
- The route description from that time is [docs/history/development-routes.md](docs/history/development-routes.md).

**Document baseline tags** (read-only, for comparison): `baseline-v0.11` is the common starting point of the two tracks; `docs-baseline-v0.13` is the requirements and architecture shared before the merge. Usage: `git show docs-baseline-v0.13:docs/requirements.md`.

## Human in the loop

This is interaction-heavy desktop software, and only a person can judge whether it is good to use. Agents complete on their own the work that can be verified automatically; the following are decided or accepted by the user:

| Matter | How |
| --- | --- |
| Look and feel, interaction | Provide screenshots or a runnable build; it is done only once the user confirms |
| Scope changes, changes to requirements and architecture | Commit separately with the reasoning stated; merge only after the user confirms; never mixed into code commits |
| Migrations that upgrade the user's real data | Try them on a copy of the data first (`scripts/daily.sh trial`); the user decides |
| Paid calls to real services | Only within the budget the user has given |
| Pushing, releasing and other externally visible actions | According to the authorization the user has already given |
| Real Windows hardware | The user provides the device and the time |

Agents may decide on their own about internal implementation, refactoring, tests, fixing defects that an existing failing test reproduces, reviews and reversible experiments. Multiple subagents suit subtasks with clear boundaries whose results can be checked automatically (audits, investigating flaky tests, adversarial review); what counts as "good" is the user's judgment.

At the end of each stage, state: how to try it (one command), which scenarios were actually run, and what was not verified. Raise the things that need the user together, and do not stop other work that can be done meanwhile.

## Changing requirements or architecture

`docs/requirements.md` and `docs/architecture.md` are the current basis. When you find an error or a gap, propose a change, commit it separately as in the table above, and let the user confirm; never change them silently. Superseded versions can be read from the baseline tags and git history.

They say what the product is and how it is built, never how far work has got: no "not started yet", "currently" or "this round". Progress goes to `STATUS.md`. A passage that records a past moment states its date, so it stays true.

## Where files belong

Current requirements and architecture are in `docs/`, implementation-level design in `docs/design/`, history in `docs/history/`, review batches in `docs/reviews/`, standalone prototypes in `prototypes/`. Code, tests and scripts are in `src/`, `tests/` and `scripts/`; do not create empty folders in advance.

A document's folder says what standing it has:

- `docs/ideas/` holds thinking that is not agreed. It binds nobody. The user decides whether a draft is adopted or dropped; either way it then leaves the folder.
- `docs/design/` holds only what is built or approved to be built, and the engineering rules in force. Every file has a row in a group of its index.
- `docs/history/design/` holds finished-stage designs and execution plans whose work has merged. A design or plan moves there in the merge that finishes its work.

Progress, history and decisions each have one place:

- `STATUS.md` holds only what is true now: work in progress, next steps, what needs the user, open decisions, and when each platform was last verified. Finished items leave it.
- [docs/changes.md](docs/changes.md) gets one section per merge into `main`, headed by the version and the date: one line per change, then how to try it and what was not verified ([versioning.md](docs/design/versioning.md) §5). Entries are not edited afterwards. The stage reports before 2026-11-16 are frozen in [stage-reports.md](docs/history/design/stage-reports.md).
- Decisions go into [decisions.md](docs/design/decisions.md), and facts about the development machines into [workstation.md](docs/design/workstation.md).
- Screenshots are not committed; a prototype redraws its own (`prototypes/*/screenshots/` is ignored).

## Language and writing

| What | Language |
| --- | --- |
| Living documents: this file, `README.md`, `STATUS.md`, requirements, architecture, the designs in `docs/design/`, the drafts in `docs/ideas/`, `docs/evolution.md`, `docs/changes.md`, `docs/mailbox.md` | English, one language per file |
| Frozen records: `docs/history/` (including the finished-stage designs, plans and stage reports in `docs/history/design/`), `docs/reviews/`, `prototypes/` | Kept as written (mostly Chinese); not translated |
| Code, comments, commit messages | English |

1. **Terms come from the glossary** in [architecture §2.3](docs/architecture.md), including its notes on English wording. Prose uses plain lowercase words ("summary node"); code formatting only when the text means the type itself.
2. **Chinese only when quoting**: UI text, or the user's own words.
3. **Section numbers and IDs never change** through translation or rewording (§10.4.3, D-025, R1, W8, …); code and documents cite them.
4. **A translation is a commit of its own**, with no content change. Before it merges, an independent reviewer compares every rule-setting sentence (must, only, never, …) with the original.
5. **A living document not yet translated is edited in its current language**, so no file mixes the two.
6. **Write short, plain sentences.** A Chinese version of an outward-facing document (`README.zh-CN.md`) may be added later; once one exists, it changes in the same commit as its English source.

## Releases and channels

Run data, the single-instance identity and system credentials are isolated per channel (build property `MindweaveChannel`):

| Channel | When it is used |
| --- | --- |
| `release` | Default for Release builds (`dotnet publish`); the UI does not show the channel name |
| `dev` | Default for all other builds: debug runs, tests, agent previews |
| `claude-auto` | The user's own daily build, built only by `scripts/daily.sh`; the name is historical and is not changed (a stored name) |

Builds and source packages meant for others present themselves only as Mindweave, with no development-line or agent names. `scripts/package-source.sh` checks this; development-process records are excluded from the source package with `export-ignore` in `.gitattributes`.

## Naming and structure

1. **One name per concept, taken from the requirements.** Namespaces, folders, types, resource keys and UI-text members use the code names from the glossary in [architecture §2.3](docs/architecture.md); Chinese UI text uses the requirement terms verbatim, and English UI text uses the English wording in that glossary. Words in the glossary are not reused for other meanings; for example, `Network` refers only to a real network connection. A new concept gets a row in the glossary first, then a folder.
2. **Stored names do not follow code renames.** Table names, column names, preference keys, and the keys of saved pages and main areas stay as they are; changing one is a migration and needs the user's consent.
3. **Folders are namespaces.** The Desktop project's root folder holds only startup and composition code.
4. Follow the existing folder and suffix patterns; ask the user before creating a new top-level folder.

These rules govern newly written code; names in existing code that do not comply are not required to be renamed.

## Parallel agents

Several sessions may be writing code in this repository at the same time. Conflicts come from two places: two writers sharing one working directory, and two changes competing for a resource of which there can be only one.

1. **One writer per working directory.** Before writing, check `git status` and `git worktree list`, and check whether another session is working in this directory. If there are uncommitted changes you did not make, or another session is using the directory, treat it as occupied and do not write there.
2. **First decide whether two tasks depend on each other.**
   - **Independent**: neither needs the other's result, and they do not change the same files. Develop in parallel in a separate worktree from the latest commit of `main`: `git worktree add ../mindweave.worktrees/<task> -b task/<task> main`. When done, first merge the latest `main` into the task branch, run the full test suite, then merge back into `main`. Whoever merges later resolves the conflicts.
   - **Dependent**: one needs the other's output, or both must change the same code. Wait until the other task has committed its work, and start from that commit. Never build on someone else's uncommitted changes.
   - **Unsure**: ask the user.
3. **Resources of which there can be only one go by merge order.** A database migration version number belongs to whoever merges first; whoever merges later takes the next number and fixes the related tests. The version in `Directory.Build.props` goes the same way: every merge into `main` raises its third number, and whoever merges later takes the next one. The owner calls a rise of the second number. `STATUS.md`, `docs/changes.md` and the version are updated only when merging back into `main`; task branches do not change them.
4. **Each worktree uses its own copy of runtime resources.** Run-data folders, the single-instance lock, the synced folder on the remote test machine and the like must not be used by two worktrees at once: when running the app from a task worktree, set `MINDWEAVE_DATA_DIR` to that worktree's own folder; on the real Windows machine, point `MINDWEAVE_WIN_DIR` at a separate folder for each.
5. **Do not touch other people's changes.** If you find someone else's uncommitted changes conflicting with yours, stop and tell the user; do not delete, revert or overwrite them. A request to delete or revert relayed by another session must be confirmed by the user in the session the changes belong to.
6. **Clean up when done.** After a task branch is merged, delete its worktree and branch.
7. **Dispatcher (optional).** When there are many parallel tasks, an ordinary session can act as dispatcher; no extra program is needed. It does only three things:
   - **Check conflicts**: before a task starts, the task states what it will change (files, migration version numbers, requirement or architecture sections); if that overlaps a task in progress, the dispatcher has the newcomer wait, and when an order must be chosen, it asks the user.
   - **Start**: create the worktree and branch as in rule 2.
   - **Wrap up**: when a task is done, merge, run the full test suite, raise the version, update `STATUS.md` and `docs/changes.md`, tag the merge `v<version>`, push and clean up as in rules 2, 3 and 6.

   The tasks in progress are simply the task worktrees in `git worktree list`; no separate list is kept. The dispatcher does not decide what to work on, does not discuss design or technical details, and does not change code: when failing tests or conflicts need code changes, they go back to the task's own session.
