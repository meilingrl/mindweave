# Security, diagnostics and upkeep

**Status:** first draft, in discussion with the owner. Last discussed: 2026-11-25. Nothing here is agreed unless a line says "decided" with a date.

This draft covers evolution D2 (security and diagnostics) and the upkeep decisions left open by the health check of 2026-11-12. It does not cover sharing, import, the exchange format or device copies (the sharing draft, on its own branch), several connections and protocols, or look, colours and accessibility.

Already decided, not reopened here: backups made before a deletion are not cleaned up automatically (D-030); `secure_delete` stays off and only `checkpoint_fullfsync` is on (2026-11-14); git history is not rewritten (D-025); no Dependabot (2026-11-14).

## 1. Standing of the open health-check items (checked 2026-11-25 against 0.6.11, `f9edc39`)

The section "需要设计、迁移或你的决定" of [low-triage.md](../reviews/claude-auto-health-20261112/low-triage.md) has 44 rows marked 待决定. Each was read again in the code and the documents. The triage file is a frozen record and is not edited.

### 1.1 Solved or outdated (5)

| Row | Why |
| --- | --- |
| storage-8 | `data-model.md` matches the schema since 0.6.9 (deletion boundaries, tables by migration) |
| requirements-4 | Requirements §9.2 and the code agree: questions and summaries share the user's priority, first come first served (`GenerationWork.cs:94`) |
| docs-8 | Requirements §5.2 now points to §8.2 for deletion; the progress lines are gone |
| gap-summary-7 | The layout session it described is gone; only changed places are saved, together (`SummaryNetworkSession.cs:94-112`) |
| gap-unprompted-6 | Now the stated rule (requirements §10.6): requests without reported usage are counted apart and never estimated |

### 1.2 Decided elsewhere (part of 1 row)

performance-7: the pruning half is D-030. The other half (each cascading delete copies the whole database) is in group B of §1.5.

### 1.3 Belongs to another topic (14; named, not discussed here)

| Topic | Rows |
| --- | --- |
| Look, keyboard and accessibility | a11y-i18n-5, -6, -7, -8, -9; performance-3 (a virtualized sidebar changes selection and keyboard handling) |
| Packaging and release | cross-platform-4 (no signature, no macOS package for others); dependencies-3 (a self-contained build carries the publishing machine's runtime patch); cross-platform-7 (needs a Windows test first) |
| Reply quality: what the model is sent | ai-client-8 (context summaries as system messages in mid-conversation); requirements-6 (the planner may summarize the latest exchange first); gap-branch-anchor-5 (table, list and code quotes lose structure) |
| Naming | gap-unprompted-3 (a same-name group is sent whole, uncapped); gap-summary-9 (same-name naming is not re-evaluated) |

### 1.4 Proposed: no longer worth a decision (6; waits for the owner's yes)

| Row | What it is | Why leave it |
| --- | --- | --- |
| gap-unprompted-7 | One price per model re-prices all past months | The cost is labelled approximate; dated prices need a migration and an editor for little gain |
| performance-5 | A page reload reads twice; hidden pages reload | No measured slowness; internal, done when that code is next changed |
| performance-9 | Each one-second checkpoint rewrites the whole answer | Same: internal, and only a measurement on long answers would justify a storage change |
| architecture-4 | Optional constructor parameters hide a forgotten registration | Internal; the agent may fix it with the next change to those services |
| architecture-5 | Exit and restore keep two hand-written save lists | Internal refactoring; same |
| storage-9 | Some cross-row rules live only in application code | Newer tables already use composite keys; the rest needs a migration and has no user effect today |

### 1.5 Needs the owner, in this discussion (19)

| Group | Rows | Question |
| --- | --- | --- |
| A. D2: safety and diagnostics | observability-7, security-2, gap-native-interop-1, dependencies-4 | §2 |
| B. Data format, upgrades, backups, exit | gap-schema-3, -4, -5, -6, -7; performance-7 (copy per delete); errors-9 | §3 |
| C. Dependencies and build | dependencies-2, ci-build-7 | §3 |
| D. Tests | ci-build-9, tests-7, tests-10, cross-platform-5 | §3 |
| E. Documents | architecture-8 (29 forwarding interfaces against the architecture's "no per-class interfaces"); docs-9 (theme and right-click menus are in no requirement) | §3 |

### 1.6 The open quick fixes (待做), for the list only

Of the 55 rows marked 待做 in the first table:

- **Closed:** docs-2, docs-4, docs-6. **Moot:** ui-vm-8, docs-3.
- **Partly done:** docs-5, docs-7, ci-build-4, ci-build-6, dependencies-7 (= repo-scripts-3), ai-client-10, tests-3, maintainability-8, gap-branch-anchor-6.
- **Still open, read again in the code:** security-4 (one unbounded SSE line, `SseReader.cs:26`), observability-5, -6, -8, ci-build-5 (= tests-4), gap-lifecycle-3, -7, cross-platform-3, gap-unprompted-10.
- **Still open, not read again** (their code did not change since the check of 0.6.4): ai-client-7, -9; requirements-7, -8; gap-summary-5, -6; gap-branch-anchor-2, -3, -4, -7, -8, -9; ui-vm-5, -9; performance-4, -8 (= maintainability-7); maintainability-6; cross-platform-8, -9; dependencies-6; gap-native-interop-7; concurrency-4, -8; gap-lifecycle-5; architecture-7; ci-build-10; repo-scripts-5, -7, -8; tests-9.

observability-5, -6 and -8 (a start line in the log, one line per storage pause, a trace of connection tests) are part of the D2 design below.

### 1.7 Defects

The check found no defect outside the rows above. Two findings inside them are worth the owner's eye before the design, because they are about data that stays behind:

- **An old model key can stay in the system's key store for good.** Retired keys are removed only when model settings are saved (`ModelSettingsService.cs:185`, the only caller of the clean-up). A key retired while a task was running stays until the next save; a key written after a backup that is later restored is never found again. Nothing lists or sweeps such items.
- **Restoring from the "newer version" page puts the newer data out of reach.** It is renamed to `.replaced-…`, which the app can list and delete but not restore (`RecoveryViewModel.cs:54`, `SqliteBackupFiles.cs:299`). [daily-build.md](../design/daily-build.md) sends the owner down this path when going back after an upgrade.

Both are handled in §2 and §3; nothing is fixed on this branch.

## 2. D2 design

Not yet discussed. What is built today (read in the code, 2026-11-25):

- **A failed request.** The user sees "Failed: category" and, beneath, `HTTP status: service text`, scrubbed and cut to about 540 characters. It can be selected in answers, tool steps and MCP errors, but not in the connection test or the web search test. There is no copy button. The service's request ID is stored in `ai_request` and shown nowhere. A failed automatic title or name shows nothing. No view lists past requests.
- **The log.** One file per day in `<data folder>/logs`, 14 kept, Information and above, every line scrubbed by shape (no known key is passed in). No start line with version, channel, system and schema. No action opens the log folder. Lines can hold file paths with the user's account name, model names, MCP server names and their error output, and IDs; no template writes conversation text, titles or addresses.
- **Keys.** The store can read, write and delete; it cannot list. The web search key and an MCP server's secrets can be removed. The model key cannot: clearing the field keeps it. Items are named per channel. Deleting the data folder or the app leaves all items.
- **Deleted content.** Rows are deleted; free pages and the write-ahead log keep the text until overwritten. Attachment originals are removed at once. A cascading delete first makes a full backup, with the removed originals beside it; these backups stay until the user deletes them. Nothing tells the user which backups still hold a deleted conversation.
- **Rendering.** Remote images are never loaded. Links in answers and summaries do not open. The one way to the browser shows the address, asks first, and allows only http and https. This part of D2 is built.

Parts to design, in this order: (1) what the user sees of a failed request; (2) the diagnostic package; (3) removing keys and keys left behind; (4) erasing deleted content together with its backups.

## 3. Upkeep decisions

Not yet discussed. Groups B to E of §1.5, each to be brought with a recommendation and its cost.

## 4. Decisions

None yet.
