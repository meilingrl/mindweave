# Upgrade path across all 10 migrations with real historical data, plus the key-value preference/model-settings state that has no schema — grade B

The core upgrade mechanism is sound and better tested than most desktop apps: verified backup first, all pending steps in one transaction, foreign keys off outside the transaction with a foreign_key_check before commit, newer databases refused without writing, a kill-during-migration test, and an older-schema in-place restore test. Fresh and upgraded databases run the same steps, so schema drift between them is impossible by construction unless a released step is edited. The gaps are around that core. A schema version is only an integer with no fingerprint, and "old" test databases are built from today's migration code, so an edited or renumbered step is undetectable (one such edit is in history). Format changes that need no DDL have no version gate. The upgrade is invisible to the user. Restore interacts badly with credentials and backup rotation. Nothing here loses data in a normal upgrade, which keeps this at B rather than C. Everything was established by reading code; nothing was executed.

**Strengths**

- Migration procedure follows SQLite's documented rebuild recipe: `PRAGMA foreign_keys = OFF` outside the transaction, one transaction for all pending steps, `foreign_key_check` before commit, `user_version` set inside the same transaction, foreign keys back on in `finally` (SqliteDatabase.cs:559-582). TRACK.md records why (finding of 2026-10-25).
- Fresh databases are created by running the same migrations 1..n (`InitializeEmpty` -> `Migrate(connection, 0)`, SqliteDatabase.cs:548-553), so there is no separate create script that could drift from the upgraded schema.
- A verified backup (VACUUM INTO, quick_check, header check, atomic rename from `.partial`) is made before any migration, and a backup failure blocks the migration (SqliteDatabase.cs:493-504; SqliteBackupFiles.cs:187-240). Tested by `Upgrade_backs_up_first_then_migrates` and `Backup_failure_prevents_migration`.
- Failure and interruption are tested for real: `Failed_migration_rolls_back_every_step_and_keeps_the_backup`, `Migration_that_leaves_a_dangling_reference_is_rolled_back`, `Killing_the_process_inside_a_migration_leaves_the_previous_version` (child process killed mid-migration), and `A_failing_upgrade_after_version_10_leaves_version_9_requests_as_they_were` (SqliteDatabaseTests.cs:94-129, 426; SchemaV10MigrationTests.cs:84).
- A newer database is refused before any write and the file hash is asserted unchanged (`Newer_schema_is_refused_without_writing`, SqliteDatabaseTests.cs:143-160). In-app restore re-checks the backup (application id, version no newer than supported, quick_check) and refuses a newer one with a specific message (SqliteBackupFiles.cs:79-108; UiText.Backups.cs:58).
- Restoring an older-schema backup is migrated through the normal open path and is tested (`An_older_schema_backup_is_restored_and_then_upgraded_with_its_own_upgrade_backup`, InPlaceRestoreTests.cs:99). If the restored copy cannot be opened, the previous database is put back.
- Migration tests seed the previous version and upgrade to the latest, so the v3 test exercises v2->v10 and the v6 test v5->v10, including request traces surviving both table rebuilds (SchemaV3MigrationTests.cs:25-46; SchemaV6MigrationTests.cs:28-42). v7 is covered inside the v6 test file.
- Enum-to-text mappings are explicit rather than `Enum.ToString` (SqlNames.cs), most backed by CHECK constraints. Preferences degrade to defaults on unknown values (AppearanceService.Parse, UsagePricesService.Parse, SourceAnchors.DeserializeLocator). Branch anchors carry a format version and a render profile with an explicit compatibility set (SourceAnchors.cs:60-79).
- Keys of queued or running tasks are protected from deletion when settings change: a replaced credential reference is retired and removed only when no unfinished task uses it (ModelSettingsService.cs:132-162; SqliteConversationStore.cs:249-263).

**Not examined**

- Nothing was executed: no build, no test run, no real upgrade of a populated database. All findings come from reading code and tests.
- Actual time and disk use of backup plus migration on a large (hundreds of MB) database were not measured.
- Windows-specific behaviour of rename and restore during an upgrade.
- SqliteReadingStateStore.cs, ProviderPresets.cs and the full ModelSettingsViewModel.cs were only skimmed or grepped, not read end to end.
- ConversationMarksService persistence (`conversations.unread` holding ids of deleted or restored-away conversations) was read only in part.
- Whether WorkspaceNameCache's in-memory `_forgotten` set survives an in-app restore session swap (it would suppress name caching for a workspace that a restore brings back) was not traced through the container lifetimes.
- The codex-steady track's schema and data directory were not inspected beyond confirming this track's channel name is `claude-auto`.
- System.Text.Json behaviour for missing constructor parameters and SQLite rename semantics with foreign keys off are stated from knowledge, not verified by running code.
- The baseline output files in the scratchpad (build, test, vuln, outdated, format) were not consulted.

### gap-schema-upgrade-path-and-settings-state-1. A schema version is only an integer: an edited or renumbered migration cannot be detected, and upgrade tests build their 'old' databases from today's migration code

- Final: **medium** (confirmed (one verifier lowered it)); auditor said high
- Where: `src/Mindweave.Infrastructure/Sqlite/SqliteDatabase.cs:488`; effort M

**Evidence.** `if (version == _catalog.LatestVersion) return Ready` (line 488) and `PRAGMA user_version = {LatestVersion}` (line 575) are the only identity a database carries. There is no migration table, name or hash. SchemaMigrations.cs line 6 says "Never edit a released step", but nothing enforces it. Every SchemaV*MigrationTests seeds the old version with `new MigrationCatalog(MigrationCatalog.Default.Migrations.Where(m => m.Version <= N))` (e.g. SchemaV3MigrationTests.cs:49, SchemaV10MigrationTests.cs:103), i.e. from the current source, not a frozen fixture. There is no tests/fixtures directory and no golden schema dump. History already has one in-place edit: commit 91bf3b8 changed v6's `summary_version.node_id` from `ON DELETE RESTRICT DEFERRABLE` to plain `DEFERRABLE` after cb91650 introduced v6. AGENTS.md rule 3 makes renumbering routine: the later-merged branch shifts its migration number. All builds of this track share one channel and default data directory (Directory.Build.props:16), with only a convention in TRACK.md line 52 to set MINDWEAVE_DATA_DIR in task worktrees.

**Failure scenario.** (1) A task-branch build with its own v11 is run once against the default data directory; the database becomes that branch's v11. A different v11 merges first and the branch's step is renumbered to v12. The track build sees user_version 11 == latest and opens the database as Ready with the wrong schema; queries fail at run time with 'no such column', and a later v12 fails or half-applies. (2) Any database created between cb91650 and 91bf3b8 still has RESTRICT on summary_version.node_id at v10; the code comment says that makes deleting a node's last version fail. (3) A future edit to a released step leaves all tests green, because the tests rebuild the old schema from the edited code.

**Recommendation.** Record applied migrations in a table (version, name, hash of the SQL) and verify it at open; on mismatch report a distinct status, never Ready. Add a test that pins a hash of each released step, plus a golden dump of `sqlite_schema` at the latest version. Keep at least one frozen, populated database file or SQL dump per released version (v8 onward is what real installs hold) and upgrade those in tests.

**Skeptic: partially confirmed, medium.** The structural claims are accurate and I found no guard that refutes them, but two of the three failure scenarios are weaker than stated, so high is overstated.

**What holds**
- **Identity is one integer.** The open path returns Ready on `version == _catalog.LatestVersion` after only `quick_check` and the `application_id` check. The migration run stamps `PRAGMA user_version = LatestVersion`. There is no migration table, name or hash anywhere in `src/`.
- **"Never edit a released step" is only a comment.** No test pins step content.
- **Upgrade tests build old databases from current code.** Every `SchemaV*MigrationTests` seeds with `MigrationCatalog.Default.Migrations.Where(m => m.Version <= N)`. There are no fixture, `.db`, `.sql` or golden files under `tests/`. The `sqlite_schema` assertions are spot checks that a named index exists or a table list matches, not a schema dump.
- **So scenario (3) is real.** An edit to an already-applied step would leave all tests green and existing databases silently divergent.
- **The design docs do not treat this as an accepted trade-off.** `docs/design/s0-skeleton-storage.md` and `data-model.md` define `user_version` as the schema version and say nothing about drift detection; I did not find it in TRACK.md either.

**What is overstated**
- **Scenario (2) is effectively impossible.** cb91650 (03:16:32) and 91bf3b8 (03:22:16) on 2026-10-28 are six minutes apart, consecutive commits of the same in-progress S4 stage. The summary store that uses the table first appears in 91bf3b8. No trial build could have been cut from cb91650, so no databa … (cut; full text in the workflow journal)

*Corrections to the finding.* - **File path:** the "never edit" comment is in `src/Mindweave.Infrastructure/Sqlite/Migrations/SchemaMigrations.cs`, not `Sqlite/SchemaMigrations.cs`.
- **"History already has one in-place edit" is misleading.** The v6 edit landed six minutes after v6 was introduced, in the next commit of the same unfinished stage, before any code used the table. It is not an edit of a released step.
- **Scenario (2) should be dropped.** No database plausibly exists with RESTRICT on `summary_version.node_id`.
- **Scenario (1) is conditional.** It requires breaking the TRACK.md data-directory convention and a specific merge order, and affects developer or tester data only.
- **Severity:** medium, not high. What survives is the missing drift detection and the lack of frozen upgrade fixtures (scenario 3), which is a latent risk rather than a present defect.

**Reproduction attempt: confirmed, medium.** The mechanism is real and reproduced with two passing xUnit tests against track/claude-auto @ 1ba95c9 (checked out detached in my own worktree).

What is confirmed:
- OpenExisting decides solely on PRAGMA application_id + user_version. `version == LatestVersion` returns Ready with no schema comparison; Migrate writes only `PRAGMA user_version`. There is no migration table, step name or hash anywhere in src (grep for schema_migration/hash found nothing; the Migration record's Name is never persisted, and Migration.Sql hides its SQL in a closure so it cannot even be hashed today).
- Scenario 1 reproduced: a database whose step 10 was a different migration opens with the default catalog as Ready (FoundVersion 10, no backup), ai_request has no workspace_id, a foreign table is present, and a v10 query fails at run time with "no such column: workspace_id".
- Scenario 2 reproduced with the real store: a database built with v6 as first committed in cb91650 (ON DELETE RESTRICT on summary_version.node_id) upgrades 6 -> 10 as Ready, still carries RESTRICT, and SqliteSummaryStore.DeleteVersionsAsync for a node's last version throws SqliteException 19 "FOREIGN KEY constraint failed"; the node remains. On today's schema the same call succeeds (existing SummaryStoreTests).
- Scenario 3 confirmed by inspection: every SchemaV*MigrationTests seeds its old version from MigrationCatalog.Default.Migrations.Where(m => m.Version <= N); no fixture directory, no .db/.sql dump, no golden sqlite_schema, no hash pin. My scenario-2 test had to reconstruct the old DDL from git history because nothing in … (cut; full text in the workflow journal)

*Corrections to the finding.* 1) File path: the migrations file is src/Mindweave.Infrastructure/Sqlite/Migrations/SchemaMigrations.cs (not Sqlite/SchemaMigrations.cs). The `PRAGMA user_version` line is 572, not 575; line 488 is correct.
2) Scenario 2 is overstated as "any database created between cb91650 and 91bf3b8": those are adjacent commits six minutes apart in one session, so realistically no such database exists outside a developer's scratch run. Its value is as evidence that a committed step has already been edited in place with no guard, not as live breakage. Also the upgraded database reports FoundVersion 6 then Ready; "still has RESTRICT at v10" is correct.
3) Scenario 1's "a later v12 fails or half-applies": half-apply is not possible; Migrate applies all pending steps in a single transaction with foreign_key_check, so a failing later step rolls back and yields MigrationFailed with a verified backup. A lat … (cut; full text in the workflow journal)

<details><summary>Reproduction</summary>

Added temporarily as tests/Mindweave.Infrastructure.Tests/ReproSchemaIdentityTests.cs (since deleted). The tests assert the faulty behaviour, so PASS = finding reproduced.

Core of test 1 (divergent step 10):
```csharp
var branch = new MigrationCatalog(MigrationCatalog.Default.Migrations.Where(m => m.Version <= 9)
    .Append(Migration.Sql(10, "some other branch's step", "CREATE TABLE branch_only (id TEXT NOT NULL PRIMARY KEY) STRICT;")));
await using (var other = _dir.Database(branch)) { Assert.True((await other.OpenAsync(Ct)).IsReady); }
SqliteConnection.ClearAllPools();
await using var database = _dir.Database();            // MigrationCatalog.Default
var opened = await database.OpenAsync(Ct);
Assert.Equal(DatabaseOpenStatus.Ready, opened.Status);
Assert.Null(opened.BackupPath);
Assert.Equal(0, await database.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM pragma_table_info('ai_request') WHERE name = 'workspace_id';"), Ct));
Assert.Equal(1, await database.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM sqlite_schema WHERE name = 'branch_only';"), Ct));
var failure = await Assert.ThrowsAsync<SqliteException>(() =>
    database.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM ai_request WHERE workspace_id IS NOT NULL;"), Ct));
```

Core of test 2 (v6 as first committed in cb91650, then upgraded by today's code):
```csharp
var v6 = MigrationCatalog.Default.Migrations.Single(m => m.Version == 6);
var asFirstCommitted = v6 with { Apply = (connection … (cut; full text in the workflow journal)
```

</details>

### gap-schema-upgrade-path-and-settings-state-2. After a restore, model settings can point at a key that was already deleted from the OS store, and the settings page still says a key is saved; keys are namespaced per channel, not per data directory

- Final: **medium** (confirmed); auditor said medium
- Where: `src/Mindweave.Desktop/ViewModels/Pages/ModelSettingsViewModel.cs:253`; effort S

**Evidence.** `HasSavedKey = true;` is set whenever a settings row exists (LoadAsync, lines 238-256); the credential store is never asked whether the reference resolves. The reference lives in the database (`service_connection.credential_ref`) and the secret in the OS store under `Mindweave.{channel}` (CredentialStores.cs:10). ModelSettingsService.SaveAsync retires the previous reference when a new key is typed and CleanUpRetiredCredentialsAsync deletes it from the OS store once no queued or running task uses it (ModelSettingsService.cs:93-104, 132-162). SaveAsync reuses `existing.CredentialRef` when no new key is typed (line 86), so saving again keeps the dead reference.

**Failure scenario.** The user replaces the API key on day 10 (old reference deleted from Keychain or Credential Manager). On day 12 they restore a backup from day 5, or an upgrade backup. The restored database's active reference is the deleted one. Settings shows the 'key saved' placeholder and Save succeeds, but every question, title and summary fails with a credential error until the user works out they must retype the key. Nothing in the restore confirmation warns of this. Second case: a copy of the data directory opened via MINDWEAVE_DATA_DIR in the same channel shares the same references; replacing the key in the copy deletes the original profile's key.

**Recommendation.** On load, and after any restore, check that the active reference resolves; show 'key missing, enter it again' instead of 'saved'. Mention keys in the restore confirmation text. Namespace the OS-store entries by data directory (AppPaths.InstanceKey already exists), or never delete a reference the current database did not create.

**Skeptic: confirmed, medium.** I could not refute the finding; every cited mechanism exists as described and nothing guards against it.

**What the code does**
- `ModelSettingsViewModel.LoadAsync` sets `HasSavedKey = true` whenever a settings row exists. It never calls the credential store, so the placeholder reads "已保存，留空则继续使用" (saved, leave blank to keep using) even when the reference is dead.
- `ModelSettingsService.SaveAsync` reuses `existing.CredentialRef` when no key is typed and never reads the store, so Save succeeds with a dead reference.
- Replacing a key adds the old reference to the retired list and immediately calls `CleanUpRetiredCredentialsAsync`. That deletes it from the OS store unless an unfinished task or the active settings use it.
- Both the active reference (`service_connection.credential_ref`) and the retired list (`app_state`) live in the database, so a restore rolls them back while the OS store is not rolled back.
- The OS store service name is `Mindweave.{channel}` only. `AppPaths.InstanceKey` exists but is used only for the single-instance pipe name.
- No restore confirmation string mentions keys, and I found no post-restore credential check.
- Neither `TRACK.md` nor `docs/design/decisions.md` records this as a known or accepted limitation. `docs/architecture.md` §7.3 specifies the retire-then-clean rule but says nothing about restore.

**Restore case:** holds as claimed. After restoring a backup older than the last key replacement, `ReadKeyAsync` gets null and every generation fails. The send pre-check in `ConversationService` only tests that a settings row exists, so question … (cut; full text in the workflow journal)

*Corrections to the finding.* - The failure is not shown as a "credential error". A missing key maps to `NotConfigured`, displayed as "尚未配置模型或密钥" (model or key not configured). The "credential service unavailable" text is a different path (store locked).
- Discoverability is better than "until the user works out": Test connection and Fetch model list with a blank key field both answer "请填写 API 密钥。" (please enter the API key). The contradiction is that the placeholder still says saved and Save still succeeds.
- Line references are off by one: reference reuse is at `ModelSettingsService.cs:87` (not 86); retire logic is 91-100.
- The old key is deleted at replace time only if no queued or running task holds it. Otherwise it waits for the next `SaveAsync`, because cleanup has no other caller in `src` (no startup sweep).
- The "upgrade backup" variant fails only if the key was replaced after that backup was taken; the res … (cut; full text in the workflow journal)

### gap-schema-upgrade-path-and-settings-state-3. Format changes that need no DDL have no version gate: an older build opens the database as Ready and then throws on rows it does not understand

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Infrastructure/Sqlite/SqliteConversationStore.cs:330`; effort M

**Evidence.** The only compatibility check is `version > _catalog.LatestVersion` -> TooNew (SqliteDatabase.cs:483). The schema is designed to grow without migrations: `message_block.kind` ("Kinds beyond 'text' are added in code", SchemaMigrations.cs:112-113), `ai_request.kind` ("checked by the application", lines 392-393), the capabilities JSON, and every `pref:*` key ("no migration needed", SqlitePreferenceStore.cs:5). The reader is strict: `ReadBlock` throws `InvalidDataException($"Unknown message block kind '{kind}'.")` (SqliteConversationStore.cs:330-334). `ParseCapabilities` deserializes a positional record with no version (SqlNames.cs:225-228): as far as I know System.Text.Json gives a missing constructor parameter `default`, so a capability added later reads `false`/null from every saved row and frozen task snapshot, not `DefaultsFor(protocol)`; unknown properties are dropped on the next save. Malformed JSON raises JsonException, which ModelSettingsViewModel.RunAsync does not catch (it catches only ModelSettingsException, ChatClientException, CredentialUnavailableException, lines 279-287). `retired_credential_refs` is read with a bare `JsonSerializer.Deserialize<string[]>` (SqliteModelSettingsStore.cs:76).

**Failure scenario.** Build N+1 adds an image or tool block kind without a migration, exactly as the comment plans. The user then opens the same data directory with build N (several trial builds are kept side by side on Windows per TRACK.md line 21). user_version is equal, so the database opens Ready; every conversation containing the new block fails to load with InvalidDataException, with no 'this data is from a newer version' message. In the other direction, a new capability whose default is `true` silently reads `false` for every model saved before it existed.

**Recommendation.** Bump user_version for any change in what stored values mean (a no-DDL migration is cheap), or add a separate data-format version in app_state that is checked like TooNew. Make ReadBlock degrade to a placeholder block rather than throw. Deserialize capabilities onto the protocol defaults (or store a version in the JSON) and treat unreadable JSON as defaults plus a log line.

**Skeptic: partially confirmed, low.** The cited code exists and says what is claimed, but nothing in the tree triggers the failure today, so this is a latent design gap rather than a present defect.

What holds up:
- The only forward-compatibility gate is the schema version: `version > _catalog.LatestVersion` returns TooNew, and an equal version returns Ready.
- `ReadBlock` throws `InvalidDataException` for any kind other than 'text', and it is called from the conversation load path.
- The migration comments do plan to add block kinds and request kinds in code, without a CHECK or table rebuild.
- `ParseCapabilities` falls back to `DefaultsFor(protocol)` only when the column is NULL; otherwise it deserializes the positional record with no version field.
- `RunAsync` in the model settings view model catches only the three named exception types.
- `retired_credential_refs` is read with a bare `Deserialize<string[]>`.
- docs/architecture.md:420 states that old software meeting a newer structure must refuse to write. A stored-format change with no version bump would bypass that guarantee, so the gap is not an accepted decision. I found nothing in decisions.md or TRACK.md that covers it.

Why medium is overstated:
- No reachable failure exists today. `TextBlock` is the only `ContentBlock` subtype, and `ReplaceBlocks` throws `NotSupportedException` for anything else, so no build can have written a non-text block.
- The one real precedent went the safe way: the new request kind 'workspace-name' shipped with migration v10, which bumped user_version, so older builds get TooNew.
- The comments say a CHECK constraint is av … (cut; full text in the workflow journal)

*Corrections to the finding.* - Path: the migration comments are in src/Mindweave.Infrastructure/Sqlite/Migrations/SchemaMigrations.cs (lines 112-113 and 392-393), not Sqlite/SchemaMigrations.cs.
- "Frozen task snapshot" is wrong for reads: generation_task.capabilities is written (SqliteConversationStore.cs:340) but production code never parses it back. The only `ParseCapabilities` caller is SqliteModelSettingsStore.cs:38; the other use is a test. Capability drift would affect only the active model_config row.
- "Exactly as the comment plans" overstates the comment. It explains why there is no CHECK constraint; it does not say new kinds ship without a user_version bump. The one precedent (request kind 'workspace-name') came with migration v10.
- The `JsonException` paths (malformed capabilities, `retired_credential_refs`) need corrupted or hand-edited JSON. The app writes both values itself with the same serializer, … (cut; full text in the workflow journal)

### gap-schema-upgrade-path-and-settings-state-4. The upgrade is invisible: the same 'opening data' line for a full backup plus migration, and no notice afterwards that data was upgraded or where the backup is

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Desktop/Services/AppStartup.cs:66`; effort S

**Evidence.** RunAsync shows `new StartupViewModel()` and awaits StartAsync (lines 62-72). StartupView.axaml:9 shows only `UiText.StartupOpening` = "正在打开数据…". Inside that await, an upgrade runs quick_check on the database, VACUUM INTO of the whole database, quick_check of the copy, then the migration transaction (SqliteDatabase.cs:478-520). `DatabaseOpenResult.BackupPath` and `FoundVersion` are set on a successful upgrade (line 520) but no Desktop code reads BackupPath (grep finds no use in src/Mindweave.Desktop); the upgrade appears only in the log (`LogMigrated`). quick_check also runs on every ordinary start (line 478). docs/design/s7-ui-polish.md line 672 states the real duration on a large database was never measured.

**Failure scenario.** A user with a large database starts a new build. The window shows 'opening data…' with no sign that an upgrade is in progress, for as long as two full reads plus a full copy take. A user who thinks it has hung force-quits; this is safe (the transaction rolls back) but the next start makes another full backup and starts over. After a successful upgrade the user is not told that the data format changed, that older builds can no longer open it, or that a pre-upgrade backup exists.

**Recommendation.** Report phases from OpenCore (checking, backing up, upgrading from vX to vY) to the startup page with a 'do not close' line. After a successful upgrade show a one-time notice with the old and new version and the backup location. Measure startup on a database of several hundred MB and decide whether quick_check belongs on every start.

**Skeptic: partially confirmed, low.** The code facts hold, but the consequences are overstated, so this is a low-severity UX gap rather than medium.

What holds:
- AppStartup.RunAsync shows a parameterless StartupViewModel and awaits StartAsync. StartupView.axaml is an indeterminate progress bar plus the fixed text UiText.StartupOpening ("正在打开数据…"). There is no phase reporting.
- OpenExisting runs quick_check on every start, before the version comparison. On an upgrade it then runs CreateVerified (VACUUM INTO plus verification of the copy) and Migrate, all inside the same await.
- A successful upgrade returns Result(Ready, version, backupPath). Nothing in Application or Desktop reads BackupPath; the only Desktop read of FoundVersion is the TooNew recovery text. The upgrade is surfaced only through LogMigrated.
- No test, decision or TRACK entry makes this intended. The requirements (requirements.md:266, 384) only demand a consistent backup, rollback, and failure handling, all of which are implemented. No requirement asks for an upgrade-in-progress or post-upgrade notice, so this is not a requirements violation.

What reduces the severity:
- The work runs on a background thread (Task.Run(OpenCore)) and the page has an animated progress bar. The window is responsive and not "not responding"; it is only uninformative.
- The backup is not hidden. Settings > Data and storage lists backups with their reason, including "升级前" rows, with restore and delete (s7-ui-polish.md:214, 648, and the g27 screenshot description). Only a proactive notice is missing.
- Force-quit is safe, as the finding itself concedes. Migration ba … (cut; full text in the workflow journal)

*Corrections to the finding.* 1. "No notice ... where the backup is" is overstated. Pre-upgrade backups are listed, labelled, and restorable or deletable in Settings > Data and storage > Backups. What is missing is only a proactive one-time notice.
2. The s7-ui-polish.md:672 citation is about the in-place restore flow ("真实窗口里的整个恢复过程 ... 实际耗时没量过"), not the upgrade path. It supports "large-database duration on the startup page was never measured" only by analogy.
3. The finding omits that the open runs on a background thread with an animated indeterminate progress bar, so the app does not look frozen to the OS. The "user thinks it has hung" scenario needs a database far larger than anything evidenced for this text-only, single-user app.
4. "Two full reads plus a full copy" is approximately right: quick_check on the source, VACUUM INTO, verification of the copy. No requirement is violated, and force-quit is safe with ba … (cut; full text in the workflow journal)

### gap-schema-upgrade-path-and-settings-state-5. From the 'newer version' recovery page, restoring an older backup moves the newer data aside, and the app can never bring it back

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Infrastructure/Sqlite/SqliteDatabase.cs:74`; effort M

**Evidence.** On TooNew the recovery page lists all backups with a restore action (StartupCoordinator.cs:68-72; RecoveryViewModel). RestoreAsync (SqliteDatabase.cs:74-97) calls ReplaceDatabaseWith, which renames the current database to `mindweave.db.replaced-<stamp>` (SqliteBackupFiles.cs:267-276), and makes no 'before restore' backup. Replaced files appear on the storage page only as rows that can be deleted: Desktop uses LeftoverDatabase only for listing and deletion (BackupDialogs.cs:301, 352; StorageDetailsViewModel.cs:69), there is no restore action, and the name does not match the backup regex. The design doc (s7-ui-polish.md, review of 2026-11-12) acknowledges such a file "may be the only copy" and only added a warning to the delete dialog.

**Failure scenario.** The user upgrades to build B (v10), works for a week, then starts older build A (v9), for example a trial build kept beside it. A shows 'data from a newer version' and a list of backups, including the v9 pre-upgrade backup; the user restores it to get going. The week of work is now in `mindweave.db.replaced-…`. Back in build B, the app opens the restored old data (and upgrades it again); the week's data shows only as 'old data replaced during a restore' with a delete button. Getting it back means renaming files by hand in the data directory.

**Recommendation.** On TooNew, do not offer restore as the obvious action (or require an explicit 'I will lose access to newer data in this build' step). When the startup path replaces a database that passes quick_check, save it as a named, versioned backup so the newer build lists and can restore it. Offer 'restore' for replaced files that pass the backup check.

**Skeptic: partially confirmed, low.** The mechanics are as reported; I could not refute them by reading the code. But the behaviour is a documented design choice, no data is destroyed, and the page already steers the user away from restoring, so medium is overstated.

**What holds**
- The startup restore path closes the database, calls `ReplaceDatabaseWith`, and reopens. It makes no "before restore" backup; only the in-app path (`RestoreInPlaceAsync` with a kept copy) does.
- `ReplaceDatabaseWith` renames the current database and its WAL/SHM to `mindweave.db.replaced-<stamp>`.
- Every not-ready status, including TooNew, gets the same recovery page with the full backup list. `RecoveryViewModel` does not filter by status, so each row has a "恢复此备份…" button.
- On the storage page a replaced file is a leftover row: `CanRestore` is true only for real backups, so it can be deleted but not restored.
- After such a restore, the newer build opens the restored older database and upgrades it again; the newer data sits only in the replaced file.

**Why it is low, not medium**
- **Deliberate and documented.** `docs/design/s7-ui-polish.md` says replaced files are listed but cannot be restored from the storage page (manual rename, as before), that the startup restore is unchanged and keeps the replaced files, and that such a file may be the only copy of a newer-version database, which is why the delete warning was added.
- **No data loss.** Nothing is deleted and nothing is cleaned up automatically. The newer build lists the file with its size and warns on delete that it may be the only copy.
- **Restore is not the obvious act … (cut; full text in the workflow journal)

*Corrections to the finding.* - "The app can never bring it back" is true only of the UI. The data is preserved intact and recoverable by renaming files, which is the documented recovery route (`s7-ui-polish.md:663`).
- The finding presents restore as the obvious action on TooNew. It is not: the page tells the user to open the data with a newer Mindweave, the primary button is Retry, and restore is a secondary button behind a confirmation that says the current database is replaced and kept renamed.
- The finding reads as a newly discovered defect. It is a known, documented trade-off: the 2026-11-12 review explicitly considered the newer-version case and chose a delete warning as the mitigation.
- Severity should be low: no data loss, explicit confirmation, narrow trigger (older build run against already-upgraded data), and the requirement in `docs/requirements.md` §9.3 is satisfied.
- The cited `StartupCoordinator.cs … (cut; full text in the workflow journal)

### gap-schema-upgrade-path-and-settings-state-6. Each restore of an older-schema backup writes a duplicate 'upgrade backup' and rotates a real upgrade backup out

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Infrastructure/Sqlite/SqliteDatabase.cs:519`; effort S

**Evidence.** A restored older-version file goes through OpenExisting, which calls `CreateVerified(connection, version, BackupReason.Migration, …)` (line 496) and then `_backups.Prune(MigrationBackupsKept)` (line 519). Prune deletes every backup with `Reason == Migration` beyond the newest 5 (SqliteBackupFiles.cs:165-180), and any file name without a suffix counts as Migration (`ReasonOf`, lines 351-357). The new backup is a byte-equivalent copy of the backup just restored, under a fresh time stamp. The test `An_older_schema_backup_is_restored_and_then_upgraded_with_its_own_upgrade_backup` asserts the duplicate is created but nothing about what is pruned.

**Failure scenario.** The user has five upgrade backups (v5, v6, v8, v9, v9). They restore the newest v9 backup in the app: a sixth, identical v9 backup is written and the v5 one is deleted. Restoring an old backup twice more removes v6 and v8. The list now holds three identical copies stamped today and labelled as upgrade backups, and the distinct older snapshots are gone without any prompt.

**Recommendation.** Skip the pre-migration backup when the file being migrated was just copied from a backup that still exists (pass that fact into OpenCore), or exclude that duplicate from the rotation count. Add a test asserting that restoring an older backup deletes no other backup.

### gap-schema-upgrade-path-and-settings-state-7. Every upgrade runs foreign_key_check over the whole database; one pre-existing dangling row blocks all future upgrades with no repair path

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Infrastructure/Sqlite/SqliteDatabase.cs:570`; effort S

**Evidence.** `if (SqliteConnections.ForeignKeyViolation(connection, transaction) is { } violation) throw new InvalidOperationException($"Migration left a dangling reference: {violation}.")` (lines 570-573) checks all tables, not only those the pending steps touched, and blames the migration. The result is MigrationFailed, whose recovery text says the upgrade was undone and offers Retry and the backups (UiText.Storage.cs:53). The pre-upgrade backup holds the same violation.

**Failure scenario.** A database has one orphan row from an external tool, a hand edit, or a past bug in code that wrote with foreign keys off. The app runs normally on the current build. On the next build with any migration, every start ends on 'data upgrade failed'; Retry fails the same way and adds another full backup, and restoring the newest backup fails the same way. The user is locked out of all data unless they keep the old build, and the message names an internal table and rowid.

**Recommendation.** Run foreign_key_check before applying migrations and report pre-existing violations as their own status with the affected table. Decide a policy for them (move the rows to a quarantine table, or continue with a warning) so that only violations introduced by the migration abort it.

### gap-schema-upgrade-path-and-settings-state-8. Migration v3 calls live application code, so the behaviour of a released step changes whenever Redaction changes

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Infrastructure/Sqlite/Migrations/SchemaMigrations.cs:164`; effort S

**Evidence.** `var scrubbed = Redaction.Scrub(detail);` inside migration 3 (lines 149-170) uses `Mindweave.Application.Diagnostics.Redaction` as it exists in the running build, not a copy frozen with the step. Fresh databases also run this step. The v3 test asserts today's `Redaction.Mask` in the output (SchemaV3MigrationTests.cs:35-36), so it follows any change.

**Failure scenario.** Redaction is tightened or its mask changed in a later build. A v2 database upgraded by that build ends up with different stored error text than one upgraded earlier, and an exception thrown by a future Redaction change on unusual input would fail the upgrade of old databases with MigrationFailed. Real exposure is small: installed data is at v8 or later according to TRACK.md.

**Recommendation.** Give migration 3 a private frozen copy of the scrub patterns it shipped with, or state in the step that it deliberately tracks live redaction and wrap the call so a failure leaves the detail unchanged rather than aborting the upgrade.

