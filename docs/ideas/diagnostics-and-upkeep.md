# Security, diagnostics and upkeep

**Status:** first draft, in discussion with the owner. §2 and §3 are the writer's proposal and wait for the owner's answer. Last discussed: 2026-11-25. Nothing here is agreed unless a line says "decided" with a date.

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

### 1.4 Six rows the writer proposed to leave (owner, 2026-11-25)

**Decided: two are left, four are fixed.**

| Row | What it is | Decision |
| --- | --- | --- |
| gap-unprompted-7 | One price per model re-prices all past months | Left. The cost is labelled approximate |
| storage-9 | Some cross-row rules live only in application code | Left until a migration touches those tables |
| performance-5 | A page reload reads twice; hidden pages reload | **Fix now** |
| performance-9 | Each one-second checkpoint rewrites the whole answer | **Fix now** |
| architecture-4 | Optional constructor parameters hide a forgotten registration | **Fix now** |
| architecture-5 | Exit and restore keep two hand-written save lists | **Fix now** |

The owner's two rules behind this, which hold beyond these rows:

- **Performance is fixed early when the fix is easy.** Nobody reports that software is a bit slow; they stop using it. Waiting for a complaint or a measurement is not a reason to leave a known cost.
- **A known architectural problem is not left.** That is a technical bottom line.

The four fixes are code and are not made on this branch. Each starts with a failing test or a measurement:

- performance-9: a checkpoint updates the text block being written instead of deleting and inserting every block (`SqliteConversationStore.cs:343`). The stored format does not change.
- performance-5: usage is computed from what the page already read; a hidden page reloads when it is shown.
- architecture-4: services that must be registered take required parameters; a test resolves every registered service.
- architecture-5: one list of what is saved before the data is closed, used by both exit and restore.

### 1.5 Needs the owner, in this discussion (19)

| Group | Rows | Question |
| --- | --- | --- |
| A. D2: safety and diagnostics | observability-7, security-2, gap-native-interop-1, dependencies-4 | §2 |
| B. Data format, upgrades, backups, exit | gap-schema-3, -4, -5, -6, -7; performance-7 (copy per delete); errors-9 | §3.1 |
| C. Dependencies and build | dependencies-2, ci-build-7 | §3.2 |
| D. Tests | ci-build-9, tests-7, tests-10, cross-platform-5 | §3.3 |
| E. Documents | architecture-8 (29 forwarding interfaces against the architecture's "no per-class interfaces"); docs-9 (theme and right-click menus are in no requirement) | §3.4 |

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

What is built today (read in the code, 2026-11-25):

- **A failed request.** The user sees "Failed: category" and, beneath, `HTTP status: service text`, scrubbed and cut to about 540 characters. It can be selected in answers, tool steps and MCP errors, but not in the connection test or the web search test. There is no copy button. The service's request ID is stored in `ai_request` and shown nowhere. A failed automatic title or name shows nothing. No view lists past requests.
- **The log.** One file per day in `<data folder>/logs`, 14 kept, Information and above, every line scrubbed by shape (no known key is passed in). No start line with version, channel, system and schema. No action opens the log folder. Lines can hold file paths with the user's account name, model names, MCP server names and their error output, and IDs; no template writes conversation text, titles or addresses.
- **Keys.** The store can read, write and delete; it cannot list. The web search key and an MCP server's secrets can be removed. The model key cannot: clearing the field keeps it. Items are named per channel. Deleting the data folder or the app leaves all items.
- **Deleted content.** Rows are deleted; free pages and the write-ahead log keep the text until overwritten. Attachment originals are removed at once. A cascading delete first makes a full backup, with the removed originals beside it; these backups stay until the user deletes them. Nothing tells the user which backups still hold a deleted conversation.
- **Rendering.** Remote images are never loaded. Links in answers and summaries do not open. The one way to the browser shows the address, asks first, and allows only http and https. This part of D2 is built.

The sections below are the writer's proposal of 2026-11-25. The owner's rule for this round: what the owner does not flag is approved.

### 2.1 What the user sees of a failed request

One failure block, the same wherever a failure is shown: an answer, a summary, a tool step, the connection test, the web search test, an MCP server.

- **Line 1:** what failed and the category, as today.
- **Line 2:** what to do, one sentence per category, with a link where a setting fixes it (a wrong key opens the model connection; a context that is too long names the output limit and the selection).
- **Details**, closed by default: the time, the model, the HTTP status, the service's request ID, the duration, and the service's own text, scrubbed. All of it can be selected.
- **Copy details** puts the same text on the clipboard, with the app version and the system. It holds no conversation text and no key.

A failed automatic title or name stays silent on the page, because the user did not start it. It is in the request list.

**Request list.** Settings gets a page “诊断” (Diagnostics). It lists the recent requests from `ai_request`: time, purpose, model, result, duration. It opens on failures only; a switch shows all. A row opens the same failure block. The list holds no content, because none is stored. A failure block in a conversation has a link to its row. Records are written best effort (D-007): when a row is missing, the block in the conversation still shows what the message itself stored.

The same page has **Open log folder** and **Export diagnostic package** (§2.2).

### 2.2 Diagnostic package

A zip file the user saves and sends by their own means. The app sends nothing anywhere.

**Built from a list of allowed fields, not by scrubbing a copy of the data.** Storage records are never serialized as they are (the E5 rule). The log is the only free text in it.

| File | Holds |
| --- | --- |
| `summary.txt` | App version and commit, channel, system and its version, interface language, schema version, storage state, sizes of the database and of the folders, counts (workspaces, conversations, messages, summaries, attachments), the list of backups with kind, size and schema version |
| `requests.csv` | The `ai_request` rows of the last 14 days: time, purpose, model, host of the service, result, status, request ID, error category, scrubbed error text, durations, token counts |
| `settings.txt` | Settings that are not secret: capabilities, limits, which tools are on, how many MCP servers and of which kind |
| `logs/` | The log files (14 days), scrubbed again at export with every key now stored, and with the home folder written as `~` |

**Never in it:** conversation text, questions, answers, summaries, titles, workspace names, attachment names or contents, saved web pages, keys, an MCP server's command, arguments, environment or headers, full addresses of fetched pages. IDs stay; they mean nothing outside this database.

**The dialog lists what will leave before the file is saved**, as the share dialog does. Two ticks, on by default: "model names and service hosts" and "logs". The dialog says in one line that the log can hold error text from services and tools, and that the package is plain files that can be read before sending.

**The log itself gets better at the same time** (the open rows observability-5, -6, -8): a start line with version, channel, system and schema; one line per storage pause, then a count; a line for each connection test. The file log is given the stored keys, so a key without a known shape is scrubbed too.

**Also from the recovery page.** When the data cannot be opened, the package is most needed. There it holds `summary.txt` and the logs only.

### 2.3 Keys

- **Remove the model key.** The model connection gets “移除密钥” (Remove key), as the web search key has. When requests are using the key, the app asks first: stop them and remove, or cancel. Afterwards the connection reads "not configured".
- **Retired keys are cleaned at start-up and when a task ends**, not only when settings are saved.
- **Keys left behind.** Both systems can list the items of one channel (macOS by service name, Windows by target prefix). At start-up the app removes every item of its channel that nothing refers to: no connection, no web search setting, no MCP server, no unfinished task. It writes a log line with the count, never the value. The cost: after restoring an older backup, a key entered later may have to be typed again; the text for that exists.
- **Leaving.** “诊断” has "Remove all keys of Mindweave from this computer", for before the app is deleted. It asks once.
- **A key store that does not answer** (gap-native-interop-1). A read waits 30 seconds, then the request fails as "the system's key store did not answer; unlock it and retry". It no longer holds a place in the queue. This covers the model key, the web search key and MCP secrets.
- **Secrets in an MCP command line.** Arguments are stored and shown in plain. The form says so in one line and points to the environment fields, which are kept in the key store.

### 2.4 Erasing deleted content for good

Today "deleted" means gone from the app and restorable from a backup. That stays the default. Erasing is a separate, explicit action.

**One mechanism: make every copy match the current data.** It does not track single conversations, so it needs no stored record and no migration.

设置 › 数据与存储 (Settings › Data and storage) gets “彻底清除已删除的内容” (Erase deleted content for good). The dialog states what it found and what it will do:

1. It names the backups and replaced databases that still hold something no longer in the current data (a conversation, a summary version or an attachment whose ID is gone), with their dates and sizes.
2. It rewrites the database file, so no deleted text is left in free pages, the search index or the write-ahead log.
3. It makes one fresh backup of the current data.
4. It deletes the backups and replaced databases of step 1, with the attachment copies beside them.

It then says what it does not reach: copies made by the system or by the user (Time Machine, a synced folder, a shared or exported file), and that a deleted file on a solid-state disk cannot be overwritten on request. It cannot be undone, and the user confirms.

The confirmation of a cascading delete gets one line: the backup made now keeps this content, and where to erase it.

`secure_delete` stays off, as decided: the rewrite does the same work once, when the user asks.

### 2.5 The renderer on model output (dependencies-4)

A formula or Markdown the model wrote wrongly must not take the app down every time that message is drawn.

- A test feeds a few hundred malformed formulas and Markdown fragments to the renderer. It runs with the normal tests.
- When drawing a message throws, the message is shown as plain text with a one-line note.
- A crash inside the native formula library cannot be caught. Only if the test finds one: the app notes which message it is drawing, and after a crash shows that message as plain text.

The quick fix security-4 (one stream line without a line break is read without limit) goes with this.

### 2.6 Failure cases

| Case | What happens |
| --- | --- |
| The package cannot be written (disk full, no permission) | A message with the reason; no partial file is left (written beside the target, then renamed) |
| The key store is locked during export | The log is scrubbed by shape only, and `summary.txt` says so |
| A log file is in use | It is read as far as it goes; the package is still made |
| The database is unavailable | Package from the recovery page: summary and logs |
| Removing a key while the store is locked | The key stays, the user is told to unlock and retry; the connection is not marked removed |
| The sweep of left-behind keys fails | A log line; start-up goes on; the next start tries again |
| Erase: not enough free space to rewrite the database | Refused before anything changes, with the space needed |
| Erase: answers or summaries are running | Asked first, as for a restore; nothing is stopped without a yes |
| Erase: the fresh backup fails | It stops there; no old backup is deleted |
| Erase: one old backup cannot be deleted | The others are deleted; the dialog names what is left; the action can be repeated |
| Erase: the app is killed half-way | Every step is safe by itself; running it again finishes |

### 2.7 Acceptance scenarios

1. A wrong key: the answer shows the category, what to do, and a link to the connection. Copy details gives a text with status and request ID and without the key.
2. The connection test fails: the same block, selectable, in Settings.
3. A title request fails: nothing on the page; one row in the request list.
4. A package is exported from a workspace whose conversations hold a marker string and whose key has no known shape. Neither the string nor the key is in any file of the package.
5. The data cannot be opened: the recovery page exports a package with summary and logs.
6. The model key is removed: the item is gone from the system's store, the connection reads "not configured", a new question says so.
7. A key is replaced while an answer runs: after the answer ends, the old item is gone.
8. An item that nothing refers to is in the store at start-up: it is gone after start-up, and the log says one was removed.
9. A conversation with branches is deleted, then erased for good: its marker string is in no file under the data folder, one fresh backup exists, and the app opens normally.
10. Erase with too little free space: refused, nothing changed.
11. A malformed formula in an answer: the message shows as plain text, the app keeps running.

### 2.8 Increments

| Increment | Holds | Needs |
| --- | --- | --- |
| 1. Failures you can read | The failure block with details and copy, everywhere (§2.1 without the list); the log's start line and the other log rows; keys given to the log scrub; Open log folder | Nothing stored changes. Screenshots for the owner |
| 2. Keys | Remove the model key; retired keys cleaned at start-up and task end; the 30-second wait; the line in the MCP form | Nothing stored changes |
| 3. Diagnostics page | The request list and the package, also from the recovery page | Nothing stored changes |
| 4. Keys left behind | Listing in both stores, the sweep, "remove all keys" | A run on Windows hardware |
| 5. Erase for good | §2.4 | A trial on a copy of the daily data |
| With any of them | §2.5 | — |

No increment needs a migration.

## 3. Upkeep decisions (writer's recommendations, 2026-11-25)

### 3.1 Data format, upgrades, backups, exit

| Row | Recommendation | Cost |
| --- | --- | --- |
| gap-schema-3 | **One number stays the gate.** A build that starts writing a value an older build cannot read (a new block kind, a new stored name, a required JSON field) raises the schema version, with a migration that may be empty. A test pins the set of stored names per schema version, so the set cannot change without the number | One small test. Such a change takes a migration number and makes an upgrade backup |
| gap-schema-4 | The start-up page says "Upgrading data; a backup is made first". After it, one notice: upgraded to which version, and where the backup is | Small. Screenshots |
| gap-schema-5 | A replaced database can be restored from the backup list like a backup. The "newer version" page says the newer data is kept and how to get it back. `daily-build.md` says the same | Small |
| gap-schema-6 | No upgrade backup is made of a database that was just restored from a backup file that still exists | Small |
| gap-schema-7 | A dangling row that was there before the upgrade no longer stops it. Only a violation the migration itself creates rolls it back. Old ones are logged and counted in the diagnostic summary; nothing is deleted automatically | Medium: compare before and after. One sentence of architecture §12.2 changes |
| performance-7 | The backup before a cascading delete stays (D-030). Its time is measured at full scale by `scripts/perf.sh`, and the dialog shows progress when it takes longer than a second | Small. If the measurement is bad, it comes back with numbers |
| errors-9 | After a failed retry the exit prompt offers a third choice, "Quit without saving", which lists what is lost. Today the only way out is to force-quit, which loses the same and more | Small. Architecture §12.3 step 5 and requirements §9.3 change |

### 3.2 Dependencies and build

| Row | Recommendation | Cost |
| --- | --- | --- |
| dependencies-2 | A `nuget.config` with nuget.org as the only source. **No lock files now:** NuGet takes the lowest version that fits, so the same commit resolves the same packages already; versions stay in `Directory.Packages.props`. Lock files are looked at again at 1.0.0, with the release build (dependencies-3) | Minutes |
| ci-build-7 | `global.json` names one SDK feature band and allows only newer patches; CI installs what `global.json` says; the analysis level is a fixed number. Raising any of them is one deliberate line | Minutes |

### 3.3 Tests

| Row | Recommendation | Cost |
| --- | --- | --- |
| ci-build-9 | The performance budgets run in CI on `main`, as a job of its own that does not block a merge, with budgets three times the local ones. It catches a large regression early. The gallery stays manual: it exists for the eye | About 5 CI minutes per merge |
| tests-7 | CI ends a hanging test after 5 minutes and names it. No coverage figure: nobody would act on the number | One flag |
| tests-10 | No new test project. The two parts no test names (`ServiceAddress`, `SelectionCarry`) get direct tests in the existing projects | Small |
| cross-platform-5 | The Windows key store tests run in CI. The macOS ones run in the full local run if they raise no prompt; this is tried once. It matters more with §2.3 | Small |

### 3.4 Documents

| Row | Recommendation | Cost |
| --- | --- | --- |
| architecture-8 | The text is behind the code. The Desktop reaches each Application service through one interface; that is the boundary where tests put a fake. The architecture says so in place of "no per-class interfaces", and a test keeps it: an interface with one implementation must be used from another project | One sentence of architecture §2, one test. Any interface the test finds unused across projects is removed |
| docs-9 | The light and dark theme (D-022) and the right-click menus get one line each in requirements §10.6 | Two lines |

## 4. Decisions

| Date | Decision |
| --- | --- |
| 2026-11-25 | §1.1 to §1.3 stand as sorted. Of the six rows of §1.4, two are left and four are fixed now. Performance is fixed early when the fix is easy; a known architectural problem is not left |

## 5. To check when building

- Whether the search index keeps deleted text until it is merged; the rewrite in §2.4 step 2 must include that merge.
- Whether the macOS key store tests raise a prompt.
- Whether hosted Windows runners allow the key store tests.
- How many of the 29 interfaces are used only inside one project.
