# Data integrity and SQLite storage — grade B

The core write path is well above course-project level: one serialized writer with BEGIN IMMEDIATE transactions, single-snapshot reads, STRICT tables, a single-transaction migration with foreign_key_check and user_version committed together, verified VACUUM INTO backups, a TooNew/Foreign/Unreadable open classification, and kill/disk-full/busy fault-injection tests. I found no path that loses committed data in normal operation. It falls short of an A because the newest feature (restore from backup, merged 2026-11-12) has real gaps: every restored database silently leaves WAL mode for good (verified empirically), the file swap is not crash-safe and a missing database is silently replaced by an empty one on next start, and restored bytes are not flushed before the originals are dropped. Separately, unindexed foreign-key child columns make message deletion quadratic (38 s measured for 8,000 of 40,000 messages on the single writer), and open-time lock/permission errors are presented as corruption with advice to restore.

**Strengths**

- Single serialized writer: SqliteWriteQueue runs every work item on one dedicated thread inside its own transaction (BeginTransaction = BEGIN IMMEDIATE), rolls back on any exception, honours cancellation only before the write starts, and drains the queue before closing (SqliteWriteQueue.cs:55-106). Every multi-table store operation I read (create conversation + messages + task, finish answer + task, create branch + source + ranges, summary save/adopt/discard/delete, scope delete, workspace delete) is one WriteAsync call, with preconditions re-checked inside the transaction.
- Migrations are forward-only, gap-checked (MigrationCatalog rejects non 1..n sequences), applied in ONE transaction together with PRAGMA user_version, with foreign_keys turned off outside the transaction and PRAGMA foreign_key_check before commit (SqliteDatabase.cs:559-582). A kill or failure leaves the database at the old version. The v6 and v10 table rebuilds follow SQLite's documented 12-step procedure; there are per-version migration tests for v3, v4, v5, v6, v8, v9, v10.
- Open-time classification is careful: application_id check, quick_check, TooNew refusal for a newer schema (no downgrade writes), foreign database refusal, new database built under a temporary name and renamed, and a pre-migration verified backup whose failure blocks the migration (SqliteDatabase.cs:460-546).
- Backups are made with VACUUM INTO on the writer between transactions (consistent, includes WAL content), written under a .partial name, re-opened and verified (quick_check, user_version, application_id) before the rename, with stale partials cleaned up (SqliteBackupFiles.cs:187-240). Path arguments for delete/discard are validated against the backup directory and name pattern (BackupPathOf, lines 360-371).
- Reads use one snapshot per ReadAsync call (explicit BEGIN/COMMIT on a read-only connection, SqliteDatabase.cs:263-289), which fixed a real torn-read bug recorded in TRACK.md (2026-10-27).
- Foreign-key design is deliberate: RESTRICT across entities, CASCADE only for dependent rows, deferred keys for the summary_node <-> summary_version cycle, and deletes written in explicit dependency order rather than relying on cascades (SqliteArchiveStore.cs:118-148, SqliteWorkspaceStore.cs:104-124). In-place restore re-checks the backup, undoes partial file moves, never creates an empty database after a failed undo in the same process (OpenExistingOnly), and startup recovery marks restored 'streaming'/'running' rows as interrupted (StartupCoordinator.cs:72).
- Fault-injection coverage is real: tests kill a child process before COMMIT and during backup, simulate disk-full, a busy writer, a mapped -shm on Windows, and failing restore swaps (tests/Mindweave.Infrastructure.Tests/Faults/*). The baseline run at scratchpad test.txt shows the Desktop suite passing (381 passed, 0 failed).

**Not examined**

- I did not run the Infrastructure test suite or any dotnet command (read-only rule); the only executions were sqlite3 CLI experiments in the scratchpad (system SQLite 3.54, not the e_sqlite3 bundled with Microsoft.Data.Sqlite 10.0.13).
- SqliteStatisticsStore.cs and SqlNames.cs were not read in detail (statistics queries, enum name mapping).
- Export (Infrastructure/Export) and the E5 exchange format.
- Application-layer callers beyond the restore/delete/startup chain: GenerationCoordinator checkpoint cadence, StorageHealth pause/recovery logic, ExitCoordinator ordering.
- Single-instance lock implementation (SingleInstance) that the storage layer depends on for exclusive access.
- Behaviour on Windows specifically (file-rename semantics during restore, Defender), and on network or cloud-synced data directories.
- The body of each migration test file; I only confirmed which versions have tests (no dedicated v7 test file exists).
- Performance of list/read queries at scale beyond the delete path (PerfSeed results were taken as stated in TRACK.md).

### storage-1. A restored database permanently leaves WAL mode: journal_mode=WAL is set only at creation, and VACUUM INTO backups are rollback-journal files

- Final: **medium** (confirmed (one verifier lowered it)); auditor said high
- Where: `src/Mindweave.Infrastructure/Sqlite/SqliteDatabase.cs:550`; effort S

**Evidence.** The only place the code sets WAL is InitializeEmpty (`PRAGMA journal_mode = WAL;`, line 550), reached for a new or schema-less file. OpenExisting (lines 460-521) never sets or checks the journal mode. Every backup is written by `VACUUM INTO $path` (SqliteBackupFiles.cs:213) and every restore copies that file into place (SqliteBackupFiles.cs:258, 278). VACUUM INTO does not carry WAL mode to its output; I verified in the scratchpad: a WAL database's `VACUUM INTO 'b.db'` copy reports `PRAGMA journal_mode` = `delete` (sqlite3 3.54). grep shows the only journal-mode assertion in tests is on a freshly created database (tests/Mindweave.Infrastructure.Tests/SqliteDatabaseTests.cs:30); no restore test checks it. docs/design/s0-skeleton-storage.md:33-34 states the design relies on WAL ('读取用短生命周期只读连接，WAL 下不阻塞写入').

**Failure scenario.** User restores any backup (startup recovery page or the in-app restore merged 2026-11-12). From then on, forever, the database runs in DELETE journal mode with synchronous=FULL: every commit (including each streaming checkpoint) needs an exclusive lock and waits for all open read snapshots; every ReadAsync waits while a commit is in progress; each commit costs extra fsyncs plus journal create/delete (TRACK.md already records fsync as the dominant cost on Windows). A read that outlasts the 5 s busy timeout turns a write into SQLITE_BUSY, which StorageFailures classifies as a storage condition (Busy). The exit `wal_checkpoint(TRUNCATE)` becomes a no-op. Nothing tells the user or the log that the mode changed.

**Recommendation.** After a successful open/migrate in OpenExisting (outside any transaction), run `PRAGMA journal_mode = WAL` and require the result to be 'wal'; log when it had to be changed. Add a test: restore a backup, then assert journal_mode is 'wal' on the live file, for both RestoreAsync and RestoreInPlaceAsync.

**Reproduction attempt: partially confirmed, medium.** The mechanism is real and I could not refute it; the severity is overstated.

What holds:
- `PRAGMA journal_mode = WAL` appears exactly once in `src/`, in `InitializeEmpty` (SqliteDatabase.cs:550). That is reached only from `CreateNew` and from the zero-length/schema-less branch of `OpenExisting`.
- `OpenExisting` (SqliteDatabase.cs:460-521) reads `application_id` and `user_version`, runs quick_check and migrates. It never reads or sets the journal mode.
- Per-connection pragmas (SqliteConnections.cs:28, :41) set only `synchronous` and `busy_timeout`.
- Backups are written with `VACUUM INTO $path` (SqliteBackupFiles.cs:213). Post-backup verification checks quick_check, `user_version` and `application_id` only.
- `ReplaceDatabaseWith` (SqliteBackupFiles.cs:248-300) does `File.Copy(backup.Path, incoming)` then `File.Move` into place. Both restore paths call it (SqliteDatabase.cs:83 and :144) and reopen without setting WAL.
- I reproduced the SQLite behaviour: the `VACUUM INTO` copy of a WAL database reports `delete`, with header bytes 18-19 = 01 01 against 02 02 on the source.
- No design decision accepts this. s0-skeleton-storage.md:33 says WAL is set at creation and "persisted in the file header", which assumes it survives. decisions.md:15 and :27 and s0:34 rely on WAL so reads do not block writes.
- TRACK.md has no entry for it, and the only journal-mode assertion in tests is on a fresh database (SqliteDatabaseTests.cs:30).

So after any restore (startup recovery page, in-app restore, or restoring the pre-migration backup after a failed migration) the live database stays i … (cut; full text in the workflow journal)

*Corrections to the finding.* - Severity should be medium, not high. There is no data-loss or durability impact; rollback journal with `synchronous=FULL` is fully crash-safe.
- "A read that outlasts the 5 s busy timeout turns a write into SQLITE_BUSY" is technically right but needs one read snapshot held for more than about 5 s, on top of Microsoft.Data.Sqlite's own retry up to the command timeout. Normal reads are short, so expect extra commit latency rather than routine Busy errors.
- "Forever" should read "until the database file is recreated".
- A third trigger is missing from the finding: restoring the pre-migration backup after a failed migration goes through the same path.
- The exit `wal_checkpoint(TRUNCATE)` no-op is harmless, not a failure.

<details><summary>Reproduction</summary>

Run in the scratchpad directory /private/tmp/claude-501/-Users-meilingluo-Developer-Projects-mindweave-worktrees-claude-auto/741bda69-6233-404f-b4a0-8bc89d0ed5bc/scratchpad/walv (system sqlite3 3.54.0, not the app's bundled SQLite):

sqlite3 a.db "PRAGMA journal_mode=WAL; create table t(x); insert into t values(1);"
sqlite3 a.db "VACUUM INTO 'b.db';"
sqlite3 a.db "PRAGMA journal_mode;"   -> wal
sqlite3 b.db "PRAGMA journal_mode;"   -> delete
xxd -s 18 -l 2 a.db  -> 0202   (WAL file-format bytes)
xxd -s 18 -l 2 b.db  -> 0101   (rollback-journal file-format bytes)

No application code was built or run.

</details>

**Reproduction attempt: confirmed, medium.** Reproduced with a test at track/claude-auto 1ba95c9. After either restore path the live database is in DELETE journal mode, and it stays that way after a full close and reopen.

- **Startup restore (`RestoreAsync`)**: the app's own write connection reports `journal_mode` = `delete`; no `-wal` file exists while live; still `delete` after close and reopen.
- **In-app restore (`RestoreInPlaceAsync`)**: identical result.
- **Behavioural consequence**: with a second connection holding an open read snapshot, a plain INSERT on the restored file fails with SQLITE_BUSY (5, "database is locked") after a 300 ms busy timeout. The same sequence on a never-restored database succeeds (control test passes). This is lock semantics, not timing.

The mechanism is as the finding states: `PRAGMA journal_mode = WAL` runs only in `InitializeEmpty`; `OpenExisting` neither sets nor checks it; backups are `VACUUM INTO` output, which is a rollback-journal file, and restore copies that file into place. No existing test asserts the journal mode after a restore, and TRACK.md does not record this as a known finding.

I lowered severity from high to medium. No data is lost or corrupted, and `synchronous=FULL` still gives durability. The impact is a silent, permanent break of a documented design invariant (s0-skeleton-storage.md relies on WAL so reads do not block writes): slower commits, reader/writer blocking, and a possible Busy storage failure only when a read outlasts the 5 s busy timeout plus the Microsoft.Data.Sqlite retry. The app's reads are short-lived, so user-visible Busy errors should be uncom … (cut; full text in the workflow journal)

*Corrections to the finding.* - **Severity**: high overstates it. There is no data loss, corruption or durability loss; the impact is degraded concurrency and commit cost, and the break of a documented design invariant.
- **"Every commit waits for all open read snapshots" / Busy errors**: mechanically right, and I reproduced the Busy with an artificial long-held reader. In the app, a Busy needs a read that outlasts the 5 s busy timeout plus the Microsoft.Data.Sqlite command retry; with short-lived read connections that should be uncommon. I did not measure it through the app's own `ReadAsync`/`WriteAsync` under load.
- **fsync cost on Windows**: plausible but not measured here.
- **Exit `wal_checkpoint(TRUNCATE)` becoming a no-op**: consistent with DELETE mode, harmless.
- **Line references**: 550, 460-521 and 213 match at 1ba95c9. I did not check the cited restore lines 258/278 by number; the copy-into-place is in ` … (cut; full text in the workflow journal)

<details><summary>Reproduction</summary>

Temporary file `tests/Mindweave.Infrastructure.Tests/ZzJournalModeReproTests.cs` (deleted afterwards). Run with:
`dotnet test tests/Mindweave.Infrastructure.Tests --filter "FullyQualifiedName~ZzJournalModeReproTests" --logger "console;verbosity=detailed"`

Key test code (class uses `TempDataDirectory _dir`, `ManualTime`, `ITestOutputHelper _out`):

```csharp
[Fact]
public async Task Startup_restore_keeps_wal()
{
    var backup = await SeedAsync();
    await using var database = _dir.Database(time: new ManualTime(Now));
    var result = await database.RestoreAsync(backup, Ct);
    Assert.Equal(DatabaseOpenStatus.Ready, result.Status);
    await AssertWalAsync(database);
}

[Fact]
public async Task In_place_restore_keeps_wal()
{
    var backup = await SeedAsync();
    string kept;
    await using (var before = _dir.Database(time: new ManualTime(Now.AddSeconds(-1))))
    {
        Assert.True((await before.OpenAsync(Ct)).IsReady);
        kept = await before.BackupAsync(BackupReason.BeforeRestore, Ct);
    }
    await using var database = _dir.Database(time: new ManualTime(Now));
    var result = await database.RestoreInPlaceAsync(backup, kept, Ct);
    Assert.True(result.Restored, $"{result.Failure}: {result.Detail}");
    await AssertWalAsync(database);
}

[Fact]
public async Task Control_reader_snapshot_does_not_block_a_commit_before_restore()
{
    await SeedAsync();
    Assert.Null(CommitWhileReaderHoldsSnapshot());
}

[Fact]
public async Task Reader_snapshot_does_not_block … (cut; full text in the workflow journal)
```

</details>

### storage-2. Unindexed foreign-key child columns make deleting messages quadratic; a workspace or large conversation delete can block the only writer for tens of seconds

- Final: **medium** (confirmed); auditor said medium
- Where: `src/Mindweave.Infrastructure/Sqlite/Migrations/SchemaMigrations.cs:56`; effort S

**Evidence.** Three columns reference message(id) with no index on the child side: message.supersedes_id (line 56), generation_task.target_message_id (line 258, v6 rebuild), reading_position.message_id (line 207). For every deleted message row SQLite must scan each of these tables to enforce RESTRICT/CASCADE. Deletes run as `DELETE FROM message WHERE conversation_id IN (...)` (SqliteWorkspaceStore.cs:120) and per member (SqliteArchiveStore.cs:85). I reproduced the relevant tables and keys in the scratchpad with 2,000 conversations / 40,000 messages / 20,000 tasks: deleting one workspace's 8,000 messages took 38.3 s; after adding indexes on the three columns the same delete took 0.11 s.

**Failure scenario.** A long-time user deletes a workspace holding a few thousand messages (or a deep conversation tree). The delete transaction occupies the single writer thread for seconds to tens of seconds: the UI waits, and every other write (streaming checkpoints and final answers of other conversations, drafts, reading positions) queues behind it. Cost grows with (messages deleted) x (messages + tasks in the whole database).

**Recommendation.** Add migration v11: `CREATE INDEX message_by_supersedes ON message (supersedes_id) WHERE supersedes_id IS NOT NULL`, `CREATE INDEX generation_task_by_target ON generation_task (target_message_id) WHERE target_message_id IS NOT NULL`, `CREATE INDEX reading_position_by_message ON reading_position (message_id)`. Add a PerfSeed budget for workspace delete. Minor siblings with small tables: summary_edit_draft.baseline_id, summary_node.current_version_id/pending_version_id.

**Reproduction attempt: confirmed, medium.** I could not refute it. The three child columns referencing message(id) have no index in any of the 10 migrations, foreign keys are on for the write connection, and both delete paths remove message rows in bulk inside a single write-queue transaction. SQLite's own query plan for deleting one message shows three full scans (reading_position, generation_task, message), so cost is (messages deleted) x (rows in those tables across the whole database).

I reproduced it independently with the real column and index definitions and the exact statement order from SqliteWorkspaceStore (2,000 conversations, 40,000 messages, 20,000 answer tasks, 2,000 reading positions; delete one of five workspaces = 8,000 messages). Without the indexes the message delete took 68 s; with the three proposed indexes, 0.11 s. The auditor's 38 s is the same order.

No mitigating guard exists:
- The existing steps (delete tasks first, null out supersedes_id) only satisfy RESTRICT; they do not avoid the per-row scans, because the scans cover the rest of the database.
- No cache_size or mmap pragma is set, so the app runs on SQLite defaults.
- The write queue is a single-reader channel, so every other write does queue behind the delete.
- Neither TRACK.md nor docs/design records this as known or accepted; data-model.md only states the RESTRICT/CASCADE policy.
- The performance tests budget reads only (list, load, context, statistics); nothing times a delete.

Medium stands. It is a latency and writer-starvation problem, not data loss, and it needs a large database plus a large delete. But the repo's own budge … (cut; full text in the workflow journal)

*Corrections to the finding.* Nothing in the finding is wrong. Three refinements:
- The three indexes are not equally important: message(supersedes_id) carries most of the cost, generation_task(target_message_id) is second, and reading_position(message_id) is minor because the table holds about one row per conversation.
- The headline numbers apply to a workspace delete at the "heavy year" scale. An ordinary single-conversation delete of around 100 messages in a 40,000-message database extrapolates to roughly 0.3-0.8 s (not measured directly), and small databases are unaffected.
- My reproduction used the sqlite3 CLI, not the app's bundled SQLite through Microsoft.Data.Sqlite; the query plan is the same, but absolute times in the app may differ.

<details><summary>Reproduction</summary>

Run with the sqlite3 CLI 3.54.0 in the scratchpad only; nothing in the repo was touched. Tables conversation, message, generation_task and reading_position were created with the real keys and indexes, then seeded with 2,000 conversations in 5 workspaces, 20 messages each (300-byte random bodies, hex-encoded), one answer task per assistant message and one reading position per conversation.

Statements, with PRAGMA foreign_keys=ON, in one transaction:
BEGIN;
DELETE FROM generation_task WHERE conversation_id IN (SELECT id FROM conversation WHERE workspace_id='w1');
UPDATE message SET supersedes_id = NULL WHERE conversation_id IN (...) AND supersedes_id IS NOT NULL;
DELETE FROM message WHERE conversation_id IN (...);
DELETE FROM conversation WHERE workspace_id='w1';
COMMIT;

Time for the DELETE FROM message step (8,000 rows):
- No extra indexes, default cache: 68.1 s real (36.3 user, 29.1 sys).
- All three proposed indexes: 0.107 s.

With a 200 MB cache, adding one index at a time:
- Only message(supersedes_id): 5.2 s.
- Only generation_task(target_message_id): 23.2 s.
- Only reading_position(message_id): 32.6 s.

So the self-scan of message dominates (roughly 25 s of the total), generation_task adds about 5-10 s, and reading_position is small at this size.

</details>

### storage-3. Restore file swap is not crash-safe: a kill between the renames makes the next start silently create an empty database

- Final: **low** (confirmed); auditor said medium
- Where: `src/Mindweave.Infrastructure/Sqlite/SqliteBackupFiles.cs:267`; effort M

**Evidence.** ReplaceDatabaseWith moves mindweave.db (and -wal/-shm) to `mindweave.db.replaced-<stamp>` (lines 267-276) and only then moves `mindweave.db.restoring` into place (line 278). The comment at lines 260-262 names the hazard ('the next start would otherwise find no database and create an empty one in its place') but the protection is an in-process catch block. On start, OpenCore does `if (!File.Exists(_paths.DatabasePath)) return CreateNew();` (SqliteDatabase.cs:388-391) without looking for `.restoring` or `replaced-*` siblings. The same applies when the undo itself fails (logged as Critical 'move it back by hand', line 423) on the recovery-page path and the app is started again. s0-skeleton-storage.md:54 promises '失败不自动恢复、不建空库'.

**Failure scenario.** Process is killed, crashes or loses power after the current database was renamed away and before the copy was renamed in (or the undo failed). Next launch: no mindweave.db, so a brand-new empty database is created and the user sees first-run onboarding. Their data sits in `mindweave.db.replaced-...` and `mindweave.db.restoring`, mentioned only on the storage page's leftovers list. A user who keeps working now has two diverging databases.

**Recommendation.** In OpenCore, when the database file is missing but `mindweave.db.restoring` or any `mindweave.db.replaced-*` exists, return a not-ready status (recovery page naming those files) instead of CreateNew. Better: write a small restore-intent marker before the first rename and finish or roll back the swap on next start.

**Skeptic: confirmed, low.** Could not refute; the code does what the finding says and nothing on the start path guards against it.

- ReplaceDatabaseWith renames mindweave.db, -wal and -shm to `mindweave.db.replaced-<stamp>` and only then renames `mindweave.db.restoring` into place. Between those renames there is no file under the real name. The only protection is the in-process catch for IOException/UnauthorizedAccessException, which a kill, crash or power loss never reaches.
- OpenCore goes straight to CreateNew when the database file is missing. CreateNew only handles its own `.creating` temp file and returns Ready. Nothing in src/ checks for `.restoring` or `replaced-*` siblings at open time; the only reader of `replaced-*` is ListLeftovers, used by the storage details page.
- The code comment itself names the hazard ("the next start would otherwise find no database and create an empty one in its place"), and the design doc promises no empty database is created on failure. I found no decision in docs/design/decisions.md or TRACK.md accepting this gap; the TRACK.md crash findings cover WAL-index mapping and lock release, not the restore swap.
- Tests cover only in-process failures through the BeforeSwapForTests seam; none simulates a missing database with restore leftovers at open.
- The undo-failure variant is also real, and slightly worse than claimed: after a failed undo, RestoreAsync returns IoError to the recovery page, and a Retry there calls OpenAsync, which reaches the same CreateNew without any restart. I did not trace the UI's Retry wiring, only the OpenAsync path.

Severity lowered from … (cut; full text in the workflow journal)

*Corrections to the finding.* - Line numbers are off by one or two: the hazard comment is at 259-261 (cited 260-262) and the design-doc promise is at s0-skeleton-storage.md:55 (cited 54).
- "Mentioned only on the storage page's leftovers list" holds for `replaced-*`; the `.restoring` file is not listed there, since ListLeftovers globs only the replaced infix.
- The undo-failure variant does not need an app restart: a Retry from the recovery page goes through OpenAsync to the same CreateNew.
- Severity is overstated given the microsecond window and that no data is destroyed; low is more accurate, medium only if the broken design guarantee is weighted heavily.

### storage-4. Restored copy is never flushed to disk before the previous database is deleted, and a zero-length database file is silently re-initialized as new

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Infrastructure/Sqlite/SqliteDatabase.cs:157`; effort S

**Evidence.** ReplaceDatabaseWith uses `File.Copy(backup.Path, incoming)` (SqliteBackupFiles.cs:258) with no flush-to-disk of the copy or of the directory. RestoreInPlaceCore then opens it and, when ready, immediately deletes the previous database files via DropReplaced (SqliteDatabase.cs:157-160). Separately, OpenExisting treats a file with application_id 0, user_version 0 and no schema objects as 'holds nothing to lose; initialize it in place' and returns Ready (lines 467-476), which is exactly what a zero-length mindweave.db looks like.

**Failure scenario.** Power loss or OS crash shortly after an in-app restore: the unflushed copy can come back truncated or empty while the originals are already deleted. A partial file is caught by quick_check (recovery page), but a zero-length file opens as a fresh empty database with no warning. The same silent path applies to any event that truncates the file to zero bytes (interrupted external copy, sync-tool placeholder). Recovery then depends on the user knowing to look in backups/.

**Recommendation.** Flush the restored copy (FileStream.Flush(true)) before the swap, and keep the replaced files until the next clean exit or next successful start. When an existing zero-length/schema-less database file is found while backups or leftovers exist, show the recovery page instead of initializing silently.

**Skeptic: partially confirmed, low.** The cited code exists and behaves as described, but the consequence is overstated: no data is lost in the claimed scenario, only a silent empty start from which the user must restore by hand.

**What holds**
- `ReplaceDatabaseWith` copies the backup with a plain `File.Copy(backup.Path, incoming)` and renames it into place; there is no flush of the file or directory anywhere in `src` (a grep for `Flush(true` / `fsync` finds none in the storage code).
- `RestoreInPlaceCore` then calls `OpenCore()`. For a backup already at the latest schema version this only reads (pragmas and `quick_check`), so nothing forces the copied pages to disk before `DropReplaced` deletes the replaced files.
- `OpenExisting` initializes a file with `application_id` 0, `user_version` 0 and no schema objects in place and returns Ready. It does not look at `backups/` or at leftover files first.

**What is overstated**
- **No data loss.** The restore source is copied, not moved, so it stays in `backups/`. The replaced files are deleted only when `File.Exists(keptCopy)` is true, and `keptCopy` is the "before restore" backup made just beforehand (`BackupDialogs.cs:206`). That backup is written by `VACUUM INTO`, integrity-checked and header-checked. So after the claimed power loss both the restored state and the pre-restore state survive as verified backups. `stage-reports.md:48` records deleting the replaced files as a deliberate decision for exactly this reason.
- **Narrow trigger.** It needs a power loss or OS crash within seconds of an in-app restore, and the file must come back at exactly zero length. A … (cut; full text in the workflow journal)

*Corrections to the finding.* - "The originals are already deleted" suggests data loss. The deleted files' content is held by the verified before-restore backup (deletion is conditional on that file existing), and the restore source also stays in `backups/`. The outcome is a silent empty start, recoverable without loss from the in-app backup list.
- The title's "never flushed before the previous database is deleted" is accurate, but the deletion is a documented design choice (`stage-reports.md:48`), not an oversight.
- Zero-length initialization is an intentional, tested S0 behaviour. The defect is narrower: it applies even when backups or leftover files exist beside the empty file.
- A zero-length result is only one of several post-crash outcomes; the others (zero-filled, truncated) reach the recovery page.
- Not checked: whether .NET's `File.Copy` uses an APFS clone on macOS, which would make the zero-length outcom … (cut; full text in the workflow journal)

### storage-5. Persistent lock, permission or disk errors at open are reported as a damaged database with advice to restore from backup

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Infrastructure/Sqlite/SqliteDatabase.cs:406`; effort S

**Evidence.** In OpenCore, any SqliteException that is not transient-and-within-3-seconds falls into `catch (SqliteException ex) { ... return Result(DatabaseOpenStatus.Unreadable, ...) }` (lines 406-410). That includes SQLITE_BUSY/LOCKED after the 3 s window, SQLITE_CANTOPEN (14), SQLITE_READONLY/PERM (8/3), SQLITE_FULL (13) and persistent SQLITE_IOERR. The UI text for Unreadable is '数据库文件损坏或不是有效的数据库…可以检查数据位置，或从备份恢复' (src/Mindweave.Desktop/UiText.Storage.cs:45). StorageFailures.Classify already distinguishes these codes for writes but is not used here. TRACK.md (2026-10-28) fixed only the transient Windows case and itself called the restore prompt 'wrong and risky'.

**Failure scenario.** Another program holds the file locked for more than 3 s (a database browser, a backup or sync tool), or the data directory became read-only, or the disk is full during WAL recovery. The user is told the database is corrupt and offered a restore; choosing it replaces a healthy current database with an older backup (the current one survives only as a `replaced-*` leftover), so recent work disappears from the app.

**Recommendation.** Classify open failures with StorageFailures.Classify: map Busy, ReadOnly, Full and IoError to distinct statuses (or IoError with a specific message) that offer Retry and do not suggest restoring; reserve Unreadable for SQLITE_CORRUPT/NOTADB and quick_check failures.

**Skeptic: partially confirmed, low.** The misclassification is real, but the claimed data-loss outcome is mostly blocked by existing guards, so I rate it low rather than medium. Nothing was executed; this is from reading the code.

**What holds.** In `OpenCore`, every `SqliteException` from opening an existing file that is not Busy/Locked/IoError inside the 3-second window is returned as `DatabaseOpenStatus.Unreadable`. There is no classification by result code: `StorageFailures.Classify` exists and separates Full, Busy, ReadOnly, IoError and Corrupt, but only the write path uses it. So CANTOPEN (14), PERM/READONLY (3/8) and FULL (13) become Unreadable immediately, and Busy/Locked/IoError do so after 3 seconds. The UI text for Unreadable says the file is corrupt or not a database and suggests restoring from backup. A correct alternative already exists and is unused here: the `IoError` status with text "check disk space and permissions, then retry". The design doc states the behaviour as intended ("after 3 seconds still failing is treated as unreadable"), so this is a known simplification, not an oversight, and no TRACK.md entry lists it as a pending decision. The only tests for Unreadable use garbage files; none covers a locked or permission-denied open.

**What is overstated.**
- **Retry is already offered.** `RecoveryViewModel` exposes Retry, Open data folder and Exit for every status, so the recommendation to "offer Retry" is already met.
- **The real cause is visible.** The detail panel shows the raw SQLite message and extended code (for example "unable to open database file (extended code 14)") under the w … (cut; full text in the workflow journal)

*Corrections to the finding.* - **Retry:** the recovery page already offers Retry for every status, so that part of the recommendation is already in place.
- **Restore prompt:** the backup list is shown for any failed-open status when backups exist, not specifically because of Unreadable; only the explanation text suggests it. Restoring needs an explicit two-step confirmation that states the consequence.
- **"Replaces a healthy database":** overstated. With a read-only directory or a full disk, the copy into the data directory fails. With a Windows file lock, the rename fails and is rolled back. All three return `IoError` with the database untouched. The outcome is reachable mainly with a POSIX lock or file-level permission problem, and the original is still kept as `mindweave.db.replaced-*`.
- **Error codes:** SQLITE_FULL at open is unlikely because opening at the current schema version only reads. A read-only file … (cut; full text in the workflow journal)

### storage-6. Pre-migration backups are pruned only after a successful migration, so each retry of a failing migration adds another full copy

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Infrastructure/Sqlite/SqliteDatabase.cs:519`; effort S

**Evidence.** OpenExisting creates a verified backup (line 496), runs Migrate (line 508) and returns MigrationFailed on an exception (lines 510-516) before reaching `_backups.Prune(MigrationBackupsKept)` (line 519). Each attempt uses a new timestamped name, so nothing is reused or removed.

**Failure scenario.** A migration fails deterministically on a user's data (for example foreign_key_check finds an old dangling row). Every app start and every 'retry' on the recovery page writes another full-size copy of the database into backups/. With a large database a handful of retries fills the disk, after which the failure changes to BackupFailed and the original cause is hidden.

**Recommendation.** Before creating a migration backup, reuse an existing verified backup of the same version if the database has not changed since (or prune after the backup is created regardless of the migration outcome, keeping the newest N).

### storage-7. Every cascade delete writes a full-database backup on the writer thread, and those backups are never rotated

- Final: **low** (not verified); auditor said low; already known in TRACK.md
- Where: `src/Mindweave.Infrastructure/Sqlite/SqliteBackupFiles.cs:171`; effort M

**Evidence.** ConversationDeletion (src/Mindweave.Application/Conversations/ConversationDeletion.cs:74) and WorkspaceDeletion (line 81) call BackupAsync before deleting; BackupAsync runs VACUUM INTO of the whole database as a writer-queue item (SqliteDatabase.cs:213-217). Prune rotates only `BackupReason.Migration` (SqliteBackupFiles.cs:171). TRACK.md records the accumulation and the manual cleanup page (item 5, merged 2026-11-12) as the decided answer.

**Failure scenario.** With a 500 MB database, deleting ten conversations that have branches or summaries writes about 5 GB of backups and stalls all other writes for the duration of each copy (synchronous=FULL). Disk use grows until the user cleans up by hand; a full disk then pauses storage.

**Recommendation.** Still a live risk worth a cap: show total backup size where deletes are confirmed, and consider an age/size limit for before-delete backups or a per-scope export instead of a whole-database copy.

### storage-8. docs/design/data-model.md no longer matches the schema and delete rules it is supposed to anchor

- Final: **low** (not verified); auditor said low
- Where: `docs/design/data-model.md:33`; effort S

**Evidence.** The transaction table lists '布局保存（修订号防回退）' but node_layout has no revision column (SchemaMigrations.cs:356-362) and SaveLayoutAsync upserts unconditionally (SqliteNetworkStore.cs:35-54). The delete boundary section says a conversation can be deleted only with no children and no summary node, while the code deletes a whole confirmed scope (SqliteArchiveStore.cs:94-153, requirements v0.12). It names `supersedes_message_id`; the column is `supersedes_id`. It says ON DELETE is used only for reading positions, drafts and layout, while ai_request, context_summary, conversation_archive and message_block also cascade. s0-skeleton-storage.md:53 says the backup copy is verified read-only; the code opens it read-write (SqliteBackupFiles.cs:218).

**Failure scenario.** A reviewer or the other track checks constraints against data-model.md and concludes a layout revision guard exists or that cascade deletes are forbidden, and builds or audits on that false basis.

**Recommendation.** Update data-model.md to the v10 schema: actual cascade list, scope-delete rule, real column names, and either implement or remove the layout revision guard claim.

### storage-9. Several cross-row invariants exist only in application code, with no database constraint behind them

- Final: **info** (not verified); auditor said info
- Where: `src/Mindweave.Infrastructure/Sqlite/Migrations/SchemaMigrations.cs:300`; effort M

**Evidence.** summary_node.current_version_id and pending_version_id reference summary_version(id) only (lines 300-301), so the schema allows a node to point at another node's version; the check is VersionBelongs in SqliteSummaryStore.cs:486. node_layout.workspace_id (line 358) and summary_node.workspace_id (line 298) duplicate the conversation's workspace with no composite key tying them together. message.error_kind and ai_request.kind are free text. Open never runs foreign_key_check outside migrations.

**Failure scenario.** A future store method or migration that forgets one of these checks stores an inconsistent row; nothing fails at write time, and the reader later throws (for example Head() 'Summary version ... is missing' or an unknown-name parse error) for the whole list.

**Recommendation.** Where cheap, back the invariant with a composite foreign key (for example UNIQUE(id, node_id) on summary_version and a two-column reference), and add a debug/test-only consistency check that runs foreign_key_check plus these invariants after each store test.

