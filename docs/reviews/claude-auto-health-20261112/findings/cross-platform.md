# Cross-platform correctness — grade B

Platform-sensitive code is written with unusual care: Windows file-locking semantics are handled and tested on real Windows, credential stores never fall back to plain text, export names are sanitised for Windows on every OS, and paths, line endings and culture are handled deliberately. The grade is held down by four things. (1) The only automated cross-platform gate, CI, has been red on the Ubuntu job for 11 consecutive pushes (2026-11-10 to 11-12) on a font-dependent pixel test, and TRACK.md does not record it. (2) The "saved means on disk" promise is not met on macOS, the primary platform, because SQLite's fullfsync is off. (3) macOS has no distributable form at all (only `dotnet run`), while Windows has a self-contained publish. (4) Several real-desktop behaviours (IME Enter, second-launch activation, DPI, credential stores) are covered only by a manual Windows checklist whose result column is blank.

**Strengths**

- Windows file-handle semantics are treated as first-class: SqliteConnections.cs:5,24,37 turns pooling off so closing releases the file, Prepare() (lines 47-60) disposes on a failed first PRAGMA, and SqliteDatabase.cs:394-405 retries SQLITE_IOERR_TRUNCATE/BUSY for 3 s before classifying a database as unreadable. Windows-only tests back this (SqliteDatabaseTests.cs:392, RestoreFailureTests.cs:53,74, AtomicFileWriterTests.cs:152-212); the latest CI run shows them running green on windows-latest.
- Credential stores are native on both platforms (WindowsCredentialStore.cs via CredReadW/CredWriteW with a correct CREDENTIALW layout; MacKeychainCredentialStore.cs via SecItem*), are namespaced per channel, and never fall back to plain text: CredentialStores.cs:25-35 throws CredentialUnavailableException on Linux instead of storing secrets somewhere weaker.
- Export file naming is cross-platform by design (SummaryMarkdown.cs:74-118): strips Windows-illegal characters on every OS, handles reserved device names including the 'AUX .txt' form, trims trailing dots and spaces, and caps by text elements and UTF-8 bytes. Exported content is normalised to LF, UTF-8 without BOM (lines 25, 63).
- AtomicFileWriter.cs writes a same-directory temp file with a fixed short name, flushes to disk, and retries the replacing move for 1.5 s on Windows sharing violations and access denials. It classifies 'in use' vs 'read-only' vs 'failed' and has an injectable Move seam so the locked-target path is testable on any OS.
- The single-instance design is sound across platforms: an exclusively opened lock file (released by the OS on process death, so no stale lock), a CurrentUserOnly named pipe keyed by a hash of the data directory, and a second TryAcquire after a failed signal for the Windows 'lock outlives the killed process' case (Program.cs:25-26).
- Data directories follow platform convention and avoid synced or roaming locations by default (AppPaths.cs:65-78: ~/Library/Application Support, %LOCALAPPDATA%, XDG_DATA_HOME), and are separated per channel.
- Keyboard conventions are OS-aware: Cmd vs Ctrl for the sidebar toggle and multi-select, with the Ctrl-click-is-right-click note for macOS (ShellView.axaml.cs:203, 272); the shortcut hint text follows (UiText.cs:33); both Ctrl+S and Meta+S are bound (NetworkHomeView.axaml:9-10).
- .gitattributes forces LF on all platforms after a CRLF checkout changed raw string literals on Windows CI; global.json rolls forward across SDK feature bands so both machines build.
- Culture handling is deliberate: persisted and parsed values use InvariantCulture (UsagePrices.cs:53, SidebarLayoutService.cs:32, backup stamps), and display-only strings use CurrentCulture.
- CI runs Linux and Windows on every push with warnings as errors, plus a stress workflow for repeating flaky tests on a Windows runner.

**Not examined**

- Nothing was built, run or tested by me; all behaviour claims come from reading code. I did query GitHub Actions read-only via `gh run list/view` and loaded the bundled libe_sqlite3 from the NuGet cache (2.1.13, which may differ from the version the project resolves) to read its PRAGMA defaults.
- IME behaviour: ComposerView.axaml.cs:14-29 sends on Enter and assumes the IME consumes its own Enter first, with no PreeditText guard. I could not verify this on real Windows or macOS input methods. Checklist item 27 (s6-windows-checklist.md:54) has no recorded result, and stage-reports.md:446 says the same question for the delete-name box was not tried on Windows.
- Real DPI and multi-monitor behaviour (per-monitor scaling, dragging between screens, 125%/150% Windows scaling). app.manifest declares no dpiAwareness and relies on Avalonia's runtime default; checklist item 30 is blank.
- Avalonia internals (Win32 Activate, macOS key routing, the Fluent theme's default font) were not decompiled; findings that depend on them are marked medium or low confidence.
- Whether macOS Keychain prompts actually appear after a rebuild on this machine (finding 4) was not reproduced.
- Trackpad and mouse-wheel feel in NetworkCanvas on Windows precision touchpads; macOS pinch-to-zoom is not handled in code (no Magnify gesture handler) but I did not assess whether that matters.
- scripts/windows.sh, mailbox.ps1 and package-source.sh (developer tooling, not product).
- The tests' own cross-platform timing assumptions beyond the OS-conditional skips.
- Behaviour with MINDWEAVE_DATA_DIR on a network share or cloud-synced folder was only reasoned about: defaults avoid such locations, and there is no detection or warning when the override points at one. Not tested.

### cross-platform-1. CI has been red on Linux for 11 consecutive pushes; the failing pixel test is still font-dependent and TRACK.md does not record it

- Final: **medium** (confirmed (one verifier lowered it)); auditor said high
- Where: `tests/Mindweave.Desktop.Tests/SourceHighlightThemeTests.cs:79`; effort S

**Evidence.** `gh run list --workflow ci --branch track/claude-auto`: every push from 2026-11-10T07:51Z (run 42778270780) to 2026-11-12T11:22Z (run 43208608079) concluded `failure`, one was cancelled; the last success was 2026-11-10T05:59Z. In the latest run, `test (windows-latest)` succeeds and `test (ubuntu-latest)` fails in step Test: `SourceHighlightThemeTests.The_line_under_the_highlight_is_drawn_along_the_bottom_of_the_highlighted_text` with "only 4 of 54 pixels above the line are the tint #ff6e5210" (line 79). Commit 4ca799c (2026-11-12) rewrote the assertion after the Windows failure (TRACK.md:94) but it still requires `tinted * 4 >= above.Count` on a row that runs through the glyph bottoms, which depends on the platform CJK font (Noto CJK on the runner). TRACK.md records only the Windows failure and reports macOS pass counts; it does not say CI is red. Separately, ci.yml:24 runs macOS only when a manual dispatch input is set, so the primary platform is never in CI by default.

**Failure scenario.** A change that breaks Linux or Windows only (the class of bug this project has hit repeatedly: handle leaks, CRLF, -shm truncation) lands while the run is already red. The red X is indistinguishable from the standing failure, so nobody opens the log, and the regression ships to the Windows test machine or the course demo. The three days of red already show the signal is not being read.

**Recommendation.** Make the test font-independent (sample a row inside the tint but above the glyph descent, or render with a bundled test font), or skip it on Linux with a stated reason, so the gate goes green. Record CI state in TRACK.md's test line next to the macOS numbers. Consider a scheduled or weekly macOS CI run so the primary platform has some automated coverage.

**Skeptic: partially confirmed, medium.** I could not refute the factual core; every cited fact checks out against the repo and GitHub.

**What holds**
- `gh run list --workflow ci --branch track/claude-auto` shows 11 failures and 1 cancelled run from 2026-11-10T07:51Z (42778270780) to 2026-11-12T11:22Z (43208608079, head 1ba95c9, the current track tip). The last success was 42765544033 at 2026-11-10T05:59Z.
- In the latest run `test (windows-latest)` succeeds and `test (ubuntu-latest)` fails in step Test, with exactly one failing test: `SourceHighlightThemeTests.The_line_under_the_highlight_is_drawn_along_the_bottom_of_the_highlighted_text`, "only 4 of 54 pixels above the line are the tint #ff6e5210" (Desktop.Tests: 1 failed, 380 passed, 58 skipped).
- The assertion at SourceHighlightThemeTests.cs:78-79 is as quoted, and nothing skips or guards it on Linux.
- TRACK.md:94 records only the Windows failure and its fix; TRACK.md:23 reports only macOS counts. Nothing in TRACK.md says CI is red or that Linux still fails after 4ca799c.
- ci.yml:24 includes macOS only when the manual dispatch input is set. There is no scheduled trigger in ci.yml or stress.yml.

**Evidence the red signal really was not read**
- Runs 42778270780, 42840640782 and 43058906488 failed on both Ubuntu and Windows with this same test from 2026-11-10 onward.
- TRACK.md:94 nevertheless says the problem was exposed by the first Windows-machine report on 2026-11-12. CI had been reporting it for two days.
- The fix 4ca799c was pushed (run 43175370696), went green on Windows only, and three more pushes including two merges followed on a red run.

**Why … (cut; full text in the workflow journal)

*Corrections to the finding.* 1. **Duration.** "Three days of red" is about 2.2 days of pushes (2026-11-10T07:51Z to 2026-11-12T11:22Z). The run has been red since the commit that introduced the test (95608ad, 2026-11-10), not since some later regression.

2. **Root cause is unverified.** The finding states as fact that the Linux failure is glyph strokes of Noto CJK. Only 4 of 54 pixels (7%) match the tint, which is far lower than strokes crossing a row would explain. The earlier single-pixel Linux reading was #96BED9, a bluish colour that is not a blend of white text (#FFFFFF) and the tint (#6E5210). The mark-colour check on the same frame passes, so it is not a channel-order problem. It may be that on Linux the row 2 px above the line is not covered by the tint at all (different line metrics), which could be a real rendering difference and not only a test artefact. This should be diagnosed before the test is skippe … (cut; full text in the workflow journal)

**Reproduction attempt: confirmed, medium.** This is a CI/process finding, so I verified it by read-only inspection (gh run list / gh run view --log-failed, git show of track/claude-auto files) rather than by a new xUnit test. The failure is Linux-only and cannot be reproduced on this macOS machine: the baseline full run here shows Mindweave.Desktop.Tests at 0 failed / 381 passed / 58 skipped, while the Ubuntu runner on the same head commit shows 1 failed / 380 passed / 58 skipped.

Every factual claim checks out:
- The 12 most recent pushes to track/claude-auto (2026-11-10T07:51Z run 42778270780 through 2026-11-12T11:22Z run 43208608079) are 11 failures and 1 cancelled. The last success is run 42765544033 at 2026-11-10T05:59Z.
- In the latest run (head 1ba95c9, the current tip of track/claude-auto), `test (windows-latest)` succeeds and `test (ubuntu-latest)` fails in step Test with exactly one failure: `SourceHighlightThemeTests.The_line_under_the_highlight_is_drawn_along_the_bottom_of_the_highlighted_text`, "only 4 of 54 pixels above the line are the tint #ff6e5210", at SourceHighlightThemeTests.cs line 79.
- Commit 4ca799c (2026-11-12 15:27 +0800) rewrote the assertion. Its own CI run (43175370696) already failed on Ubuntu with the identical 4-of-54 message while Windows went green, so the fix was verified for Windows only and the Linux result was not read.
- The test still samples the row at line.Top - 2 and requires `tinted * 4 >= above.Count`. The test's own comment says this row runs through the glyph bottoms and depends on the platform CJK font.
- TRACK.md line 94 records only the Windows failure and the rewri … (cut; full text in the workflow journal)

*Corrections to the finding.* - The count is 12 pushes since the last green run (11 failure, 1 cancelled). "11 consecutive failures" is accurate only if the cancelled run is set aside.
- The font-dependence root cause is plausible and matches the test's own comment, but it is not proven for Linux. The log shows only that 4 of 54 sampled pixels matched the tint under Noto CJK with headless rendering; I could not render on Linux to confirm where the glyph strokes fall.
- The finding understates one point: the first red run (2026-11-10) failed this test on both Windows and Ubuntu, two days before TRACK.md says the Windows tester "exposed" it.
- The "indistinguishable red X" claim is overstated at job level. The Windows job is a separate green check, so a new Windows failure would be a visible change; the masking applies to the overall run status and to any new Linux failure.
- Linux is not a shipping platform, and macOS … (cut; full text in the workflow journal)

<details><summary>Reproduction</summary>

No xUnit test was written. The finding is about CI state and documentation, and the failure occurs only on the Ubuntu runner, so it cannot be reproduced on this macOS machine. I ran no build or test and made no paid or remote calls beyond read-only `gh`.

Read-only output from the latest run (`gh run view 43208608079 --log-failed`):

```
test (ubuntu-latest) 2026-11-12T11:26:39Z [xUnit.net 00:02:23.37] Mindweave.Desktop.Tests.SourceHighlightThemeTests.The_line_under_the_highlight_is_drawn_along_the_bottom_of_the_highlighted_text [FAIL]
   only 4 of 54 pixels above the line are the tint #ff6e5210
   at ...SourceHighlightThemeTests.cs:line 79
Failed!  - Failed: 1, Passed: 380, Skipped: 58, Total: 439, Duration: 2 m 37 s - Mindweave.Desktop.Tests.dll (net10.0)
##[error]Process completed with exit code 1.
```

Jobs for that run: `test (ubuntu-latest) failure [Test]`, `test (windows-latest) success`.

`gh run list` shows 11 failures and 1 cancelled since the last success at 2026-11-10T05:59:42Z.

The worktree is clean: `git status --short` is empty, and the one scratch file I created outside the repo has been deleted.

</details>

### cross-platform-2. On macOS, SQLite commits are not flushed to the physical disk (fullfsync off), so the documented "saved means on disk" promise does not hold on the primary platform

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Infrastructure/Sqlite/SqliteConnections.cs:28`; effort S

**Evidence.** The only durability pragmas are `PRAGMA synchronous = FULL; PRAGMA busy_timeout = ...` (line 28); nothing in src/ sets `fullfsync` or `checkpoint_fullfsync` (grep). docs/design/decisions.md:15 justifies FULL as buying the promise "保存成功即落盘". On macOS, fsync() does not flush the drive's write cache; SQLite only issues F_FULLFSYNC when `PRAGMA fullfsync=ON`. I loaded the bundled libe_sqlite3 (SQLitePCLRaw.lib.e_sqlite3 2.1.13, osx-arm64) and it reports `PRAGMA fullfsync` = 0 and `checkpoint_fullfsync` = 0. TRACK.md:97 observes the symptom without the cause: "macOS 上这一步几乎不花时间，Windows 要等磁盘". By contrast, .NET's own `Flush(flushToDisk: true)` in AtomicFileWriter.cs:56 and SqliteDiskProbe.cs:20 does use F_FULLFSYNC on macOS, so exports are more durable than the database.

**Failure scenario.** A MacBook loses power or kernel-panics shortly after an answer's terminal write, a summary version save or a pre-delete backup reported success. The last commits may be missing. Because WAL frame ordering is not guaranteed without a full flush, SQLite's documentation also allows a corrupted database, which the app would surface as the recovery page. The same sequence on Windows is safe (FlushFileBuffers).

**Recommendation.** Add `PRAGMA fullfsync = ON; PRAGMA checkpoint_fullfsync = ON;` to the read-write connection pragmas (a no-op off macOS). Measure the cost on the Mac: writes are sparse by design, and tests can lower it. If the cost is judged too high, change the promise in decisions.md rather than leaving it stated but unmet.

**Reproduction attempt: partially confirmed, low.** I could not refute the factual core; the severity is overstated.

**What holds**
- The read-write connection sets only `PRAGMA synchronous = FULL` and `busy_timeout`. Nothing in src/, tests/, docs/ or TRACK.md mentions `fullfsync`, `checkpoint_fullfsync` or F_FULLFSYNC, so there is no guard and no recorded decision accepting the gap.
- I loaded the bundled osx-arm64 `libe_sqlite3.dylib` (SQLitePCLRaw.lib.e_sqlite3 2.1.13, SQLite 3.53.3) myself. `PRAGMA fullfsync` and `PRAGMA checkpoint_fullfsync` both return 0.
- With those off, SQLite on macOS calls plain `fsync()`, which does not force the drive's write cache to stable storage.
- decisions.md:15 justifies FULL over NORMAL specifically by power loss ("只在断电时可能丢最后几笔") and states the promise "保存成功即落盘". On macOS, FULL without fullfsync gives power-loss durability no better than what the document rejected.
- TRACK.md:97 records the symptom (sync costs almost nothing on macOS, Windows waits for the disk) without naming the cause.
- The export path and disk probe use `Flush(flushToDisk: true)`, which .NET maps to F_FULLFSYNC on Apple platforms, so the inconsistency with the database is real.
- The recommended pragmas are a no-op off macOS.

**Why I lowered it to low**
1. The trigger is narrow: sudden power loss, or a hard panic or reset that stops the drive flushing its cache. Mac laptops have batteries, and a process crash or force-quit is unaffected because committed WAL frames are already in the OS cache. The realistic exposure is a desktop Mac losing mains power or a forced power-off.
2. The realistic loss is the last few com … (cut; full text in the workflow journal)

*Corrections to the finding.* - Severity should be low, not medium: the loss is bounded to the last few commits under sudden power loss or hard reset on macOS only.
- The corruption outcome ("recovery page") is overstated. WAL frame checksums make loss of the trailing commits the expected result; corruption requires cross-file write reordering by the drive around a checkpoint and is a tail risk.
- "Kernel panic" as a trigger is uncertain. A panic does not necessarily cut drive power, so data already handed to the drive by `fsync()` may still persist. Power loss or a forced power-off is the solid trigger.
- "Primary platform" is not established by anything I checked; the product targets Windows and macOS.
- Line references and the pragma values are accurate.

<details><summary>Reproduction</summary>

Read-only probe of the bundled native library via Python ctypes on an in-memory database (no project files touched):

sqlite3_libversion() -> b'3.53.3'
PRAGMA fullfsync -> [(b'fullfsync', b'0')]
PRAGMA checkpoint_fullfsync -> [(b'checkpoint_fullfsync', b'0')]

No power-loss reproduction was attempted; it is not feasible in a read-only audit.

</details>

### cross-platform-3. A relaunch while the previous instance is still shutting down is acknowledged as "activated" and exits silently; no window ever appears

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Infrastructure/Hosting/SingleInstance.cs:157`; effort S

**Evidence.** The pipe listener writes Acknowledge before calling onActivate (lines 155-159) and keeps running until `Instance.DisposeAsync`, which AppHost.DisposeAsync runs last, after generation shutdown, flushes, WAL checkpoint and database close (AppHost.cs:59-73). onActivate posts `MainWindow.BringToFront`, which returns immediately when `Exit.Stage == Releasing` (MainWindow.axaml.cs:147-152), and after the UI loop ends the post is dropped anyway. Program.cs:19-23 treats `Activated` as success and returns 0. AppHostLifecycleTests.cs:48-60 asserts only that the closed window stays hidden; nothing covers what the second launcher is told.

**Failure scenario.** The user closes the window and immediately double-clicks the app again, a common way to restart. The first process is in Releasing, still holding the lock while it flushes and closes the database (longer on Windows, where each sync waits for the disk per TRACK.md:97, and up to StopWait = 5 s more if AppHost has to stop generation itself). The second process gets the ack, exits 0, the first finishes exiting, and nothing is running. The user has to launch a third time.

**Recommendation.** Do not acknowledge once the exit is releasing: stop the listener (or have it reply with a distinct 'exiting' byte) before the release steps. In Program.Main, on 'exiting' or no response, poll TryAcquire until the old process is gone (bounded) and then start normally, instead of the single retry.

**Skeptic: partially confirmed, low.** The mechanism is real and I could not find a guard against it, but the exposure window is much narrower than the finding implies, so medium is overstated.

What holds:
- The listener writes Acknowledge before calling onActivate and has no knowledge of exit state. It only stops in SingleInstance.DisposeAsync, which AppHost.DisposeAsync runs in its finally block after AppSession.EndAsync (generation shutdown, storage-health stop, flushes, database close, container disposal).
- onActivate is `Dispatcher.UIThread.Post(window.BringToFront)`. BringToFront returns early when Exit.Stage is Releasing, and once the UI loop has ended the post is dropped (AppHost.Run's own comment says so).
- Program.Main returns 0 on Activated with no further check. The fallback TryAcquire retry only runs on PrimaryNotResponding.
- So a second process that connects between `Stage = Releasing` and the listener's cancellation gets an ack, exits 0, and no window appears. No test covers what the second launcher is told; SingleInstanceTests only cover a live holder and a silent holder.
- The design record shows the Releasing guard was added to stop Show() throwing on a closed window (s6-wrapup.md:53). It treats the request as ignorable and never considers the launcher's outcome, so this is an unconsidered consequence, not an intended behaviour. Requirements line 264 ("repeat launch returns to the existing window") is not met in this window. TRACK.md has no entry for it.

Why the severity drops:
- On the normal exit path, Releasing is set only after the coordinator has already done the bounded generation st … (cut; full text in the workflow journal)

*Corrections to the finding.* - AppHost.cs is at src/Mindweave.Desktop/AppHost.cs, not under Infrastructure/Hosting. The cited line numbers (59-73) are correct for that file.
- "Up to StopWait = 5 s more" does not apply to the stated scenario. When the user closes the window, the coordinator reaches Releasing and AppHost uses a zero wait. The 5 s wait applies only when the loop ends without the coordinator releasing, and on that path the miss comes from the stopped dispatcher dropping the post, not from the Releasing guard.
- "Still holding the lock while it flushes and closes the database" overstates the duration. The real stop and save run before Releasing; what remains is idempotent re-flushes, the database close and container disposal, normally well under a second.
- The TRACK.md:97 claim about Windows sync cost was not checked, and I did not measure the window's actual length on either platform.
- Severity shoul … (cut; full text in the workflow journal)

### cross-platform-4. macOS, the primary platform, has no distributable form: no .app bundle, Info.plist, icon or code signature, so Keychain access is tied to an unsigned per-build binary

- Final: **low** (confirmed in part); auditor said medium
- Where: `docs/design/run-guide.md:21`; effort M

**Evidence.** The run guide gives `dotnet run --project src/Mindweave.Desktop -c Release` as the only way to run on macOS (line 21); the only publish path is "Windows 发布" with `-r win-x64 --self-contained` (line 39; scripts/windows.sh:84). Mindweave.Desktop.csproj has no RuntimeIdentifier, bundle or icon settings. No .icns/.ico/Info.plist exists under src/, and no `Icon=` in MainWindow.axaml. MacKeychainCredentialStore.cs:69-75 adds items with SecItemAdd to the legacy login keychain without an access group, so the item's ACL names the creating binary; that binary is the apphost in bin/, which is at most ad-hoc signed and changes identity on every rebuild.

**Failure scenario.** (a) A non-developer cannot run the app on a Mac without installing the .NET SDK and using a terminal. (b) After any rebuild, the binary no longer matches the Keychain item's ACL, so reading the API key is expected to raise the system prompt "Mindweave wants to use your confidential information…" requiring the login password; denying it surfaces as "凭据服务不可用" (Check, line 102-108). I did not reproduce this on the machine. (c) Without a bundle identifier, macOS privacy consent for writing the export temp file beside a target in Desktop/Documents is attributed to whatever launched the process.

**Recommendation.** Add a macOS publish step mirroring Windows: self-contained osx-arm64 (and x64) output wrapped in a .app with Info.plist (CFBundleIdentifier per channel, NSHighResolutionCapable, icon) and at least a stable ad-hoc or Developer ID signature, so Keychain ACLs and privacy consent attach to a stable identity. Add a window/app icon on both platforms. Write a macOS acceptance checklist equivalent to s6-windows-checklist.md.

**Skeptic: partially confirmed, low.** The packaging gap is real, but the Keychain failure scenario (b), which carries most of the claimed severity, is contradicted by the binaries on disk.

**Confirmed**
- The run guide gives `dotnet run` as the only macOS run path, and the only publish section is "Windows 发布" (`-r win-x64 --self-contained`). `scripts/windows.sh publish` is the only publish script; there is no macOS equivalent.
- `Mindweave.Desktop.csproj` has no RuntimeIdentifier, ApplicationIcon or bundle properties. No `.icns`, `.ico`, `Info.plist` or entitlements file is tracked, and no window icon is set in `src/`.
- No macOS counterpart to `s6-windows-checklist.md` exists in `docs/design/`.
- I found no decision in TRACK.md, `decisions.md`, requirements or architecture that defers or scopes out macOS packaging, so the gap is undocumented rather than intended.
- `MacKeychainCredentialStore.Write` does call `SecItemAdd` with no access group and no data-protection-keychain flag, so the item goes to the legacy login keychain with an ACL tied to the creating code identity.

**Refuted: "changes identity on every rebuild"**
- `codesign -dvvv` on the two existing apphosts shows the same identity: `bin/Release/net10.0/Mindweave` (built Nov 12 15:25) and `bin/Debug/net10.0/Mindweave` (built Nov 12 21:14) both have Identifier=apphost, Signature=adhoc and CDHash=21c50774ced39c358aaca557c03d1acb29b26e53.
- The apphost is the SDK's template binary with only the DLL name patched in, so its code hash does not depend on the app's code. It is stable across rebuilds and configurations.
- It would change only when the SDK's … (cut; full text in the workflow journal)

*Corrections to the finding.* - "That binary ... changes identity on every rebuild" is wrong. The ad-hoc apphost has the same CDHash in the Debug and Release outputs built hours apart. Its identity changes only when the SDK's apphost pack changes.
- Scenario (b) should therefore read: a one-time Keychain prompt may appear after a .NET SDK update, or when moving to a differently built binary such as a future self-contained publish. It is dismissable with "Always Allow", and denial surfaces as "credential service unavailable". It does not occur on each rebuild.
- "A non-developer cannot run the app on a Mac" is accurate, but no such user is in the current requirements: macOS is the owner's own development and use machine, and the course deliverable is the Windows build plus a source package.
- Scenario (c) is unverified and has no functional consequence.
- Severity should be low, not medium: this is a packaging and pol … (cut; full text in the workflow journal)

### cross-platform-5. The two native credential stores (hand-written P/Invoke) have no automated coverage on any platform

- Final: **low** (confirmed in part); auditor said medium; already known in TRACK.md
- Where: `tests/Mindweave.Infrastructure.Tests/Chat/PlatformCredentialStoreTests.cs:14`; effort S

**Evidence.** The only test touching WindowsCredentialStore or MacKeychainCredentialStore is skipped unless `MINDWEAVE_CREDENTIAL_TESTS=1` (line 14). ci.yml never sets it; the latest CI log shows `Skipped ...PlatformCredentialStoreTests.Write_read_update_and_delete_round_trip` on Ubuntu, and macOS is not in the default matrix (ci.yml:24). TRACK.md:124 notes it cannot run over SSH on the Windows machine (error 1312), and TRACK.md:94 notes it is opt-in and needs the user's consent on the Windows desktop. Both stores marshal structs and CoreFoundation dictionaries by hand (WindowsCredentialStore.cs:113-128; MacKeychainCredentialStore.cs:139, 183-215).

**Failure scenario.** A refactor reorders a field in the `Credential` struct, changes a `nint`/`int` in a CoreFoundation signature, or a runtime update changes LibraryImport marshalling. The whole suite stays green on all three OSes and the break is found only when a user's API key cannot be saved or read: every question fails with a credential error.

**Recommendation.** Set MINDWEAVE_CREDENTIAL_TESTS=1 in the Windows CI job and in the opt-in macOS job; the test already uses a throwaway GUID namespace and deletes what it writes, and hosted runners have a usable credential session. Add it to the Mac-local full run as well.

**Skeptic: partially confirmed, low.** The factual core holds. The only test that exercises either native store is `PlatformCredentialStoreTests.Write_read_update_and_delete_round_trip`, and it skips unless `MINDWEAVE_CREDENTIAL_TESTS=1`. A repo-wide grep finds that variable only in the test itself and in TRACK.md: no workflow, script or run configuration sets it. ci.yml has no such env entry and macOS is only in the matrix on a manual dispatch. The only other test reference to the two classes is `SeamTests`, a source-text allow-list that executes nothing. The marshalling is hand-written as described.

I could not refute it, but the severity is overstated for three reasons:

1. **It is a known, deliberate opt-in, with manual verification on record.** The test's own doc comment says it is opt-in because it writes to the signed-in user's secret store. TRACK.md:94 and :124 already record the gap. stage-reports.md records a macOS Keychain read/write/delete run and a user-confirmed Windows Credential Manager save on 2026-10-26. The Windows acceptance checklist row 24 assigns this test to the desktop session.
2. **The code has not moved since it was verified.** `git log` on `src/Mindweave.Infrastructure/Credentials` shows a single commit (c20a37a, 2026-10-26), the same date as the manual verification. The scenario needs a future refactor of 382 lines of stable code, or a runtime change to LibraryImport blittable marshalling, which is unlikely.
3. **A break would be loud and recoverable.** Both stores map failures to `CredentialUnavailableException` with no plaintext fallback, so the user sees a clear credential error; … (cut; full text in the workflow journal)

*Corrections to the finding.* - "No automated coverage on any platform" should read "no coverage in any default or CI run". An automated round-trip test exists and has been run manually on macOS; the Windows tester can run it in a desktop session with the user's consent.
- The finding omits that the gap is already recorded (TRACK.md:94, :124; s6-windows-checklist row 24) and that the opt-in is a deliberate choice, documented in the test, to avoid writing to the user's real secret store by default.
- The failure scenario is hypothetical: the credential code has one commit and has not changed since it was manually verified on both platforms. A break would give a loud credential error on first use, with no data loss.
- The recommendation's claim that hosted runners have a usable credential session is unverified in this repo. It is plausible for the hosted Windows and macOS runners, but TRACK.md:124 shows session type ma … (cut; full text in the workflow journal)

### cross-platform-6. Start-up failures before the window exists are invisible on GUI launches, and a non-IOException while taking the lock crashes without any message

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Desktop/Program.cs:29`; effort S

**Evidence.** When another process holds the lock and does not answer, the only feedback is `Console.Error.WriteLine(...)` followed by `return 2` (lines 29-30). The project is `<OutputType>WinExe</OutputType>` (Mindweave.Desktop.csproj:3), so on Windows there is no console; the same is true for a Finder or Dock launch on macOS. `SingleInstance.TryAcquire` (SingleInstance.cs:45-55) calls Directory.CreateDirectory and opens the lock file but catches only IOException; a read-only or permission-denied data directory throws UnauthorizedAccessException, which propagates out of Main before the session, crash logger or window exist (the handlers are registered at Program.cs:36-37, after this point).

**Failure scenario.** (1) The first instance is hung, or a different TMPDIR or a differently spelled MINDWEAVE_DATA_DIR gives the second launch a different pipe path or name. The user double-clicks Mindweave.exe, waits 5 s, and nothing happens: no window, no dialog, no log line. (2) %LOCALAPPDATA%\Mindweave\<channel> or the override directory is not writable (restored profile, read-only share): the process dies with an unhandled exception that only the Windows Event Log or a terminal would show.

**Recommendation.** Catch UnauthorizedAccessException alongside IOException at the lock and report both cases through a minimal native message (start Avalonia just to show a single error window, or a MessageBox/NSAlert), and write a line to a log in the temp directory when the data directory itself is unusable.

**Skeptic: partially confirmed, low.** The cited code exists and behaves as claimed, and I found no guard, test or recorded decision that makes it intended. The triggers are narrower than the finding suggests, so I rate it low rather than medium.

**What holds**
- **Invisible message.** `Program.cs:29-30` reports the "already running but did not respond" case only through `Console.Error.WriteLine` and `return 2`. The project is `OutputType` WinExe (`Mindweave.Desktop.csproj:3`), so a double-click launch on Windows has no console, and a Finder or Dock launch on macOS shows no stderr either.
- **Design gap.** `docs/design/s0-skeleton-storage.md:27` says the second process "提示'已有实例无响应'后退出" (shows a notice, then exits). In the shipped configuration that notice is not visible to the user.
- **Uncaught exception at the lock.** `SingleInstance.TryAcquire` calls `Directory.CreateDirectory` at line 45, outside the `try`, and the `catch` at line 52 is `IOException` only. A permission-denied data directory throws `UnauthorizedAccessException` from either the create or the `FileStream` open, and it propagates out of `Main`.
- **No handler yet.** The `UnhandledException` and `UnobservedTaskException` handlers are registered at `Program.cs:36-37`, after the lock. The logger lives under `DataDirectory/logs`, so it could not write in that case anyway.
- **Not already known.** `TRACK.md` and `docs/design` have no entry for this; the only related finding (`TRACK.md:120`) covers the retry after a killed holder. The only test of this path asserts the enum result, not user-visible feedback (`SingleInstanceTests.cs:65`).

**What is o … (cut; full text in the workflow journal)

*Corrections to the finding.* - A primary with a hung UI thread still acknowledges the pipe request, so the second launch exits 0 silently instead of reaching `Program.cs:29`. The cited line is reached only when the holder is not yet listening (start-up longer than 5 s before `App.axaml.cs:46`), when the pipe name or path differs, or when a non-sharing `IOException` is misread as "another instance".
- The `TMPDIR` and "differently spelled `MINDWEAVE_DATA_DIR`" triggers are developer edge cases: `InstanceKey` already normalises to a full, upper-cased path on Windows and macOS.
- The "restored profile, read-only share" trigger is rare, and the app cannot run in that state anyway; the defect is the missing diagnostic.
- Severity is better stated as low: a diagnosability and design-conformance gap with no data-integrity impact.
- Additional point the finding missed: because `Directory.CreateDirectory` is outside the `try … (cut; full text in the workflow journal)

### cross-platform-7. On Windows a second launch probably cannot bring the existing window to the foreground (no AllowSetForegroundWindow hand-off)

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Desktop/Views/MainWindow.axaml.cs:160`; effort S

**Evidence.** BringToFront runs `Show(); Activate();` in the primary process (lines 154-160) after a one-byte pipe message. Windows only lets a process take the foreground if it owns it or was granted it; the second process, which has foreground rights because the user just started it, never calls AllowSetForegroundWindow for the primary. The only user32 import in src/ is MessageBeep (SystemAlertSound.cs:40). The run guide promises "第二次启动会把已打开的窗口带到前面" (run-guide.md:33). The only verification is manual checklist item 22 (s6-windows-checklist.md:45), whose result column is empty; SingleInstanceTests cover the pipe, not real activation. I did not run this on Windows.

**Failure scenario.** Mindweave is open behind a browser on Windows 11. The user double-clicks Mindweave.exe again. Expected by Windows foreground rules: the taskbar button flashes and the window stays behind (or is restored from minimized without focus), so the second launch appears to do nothing.

**Recommendation.** Have the Windows item 22 run on the real machine first. If confirmed, send the primary's process ID over the pipe before the request and call AllowSetForegroundWindow(pid) in the secondary (Windows only), or use the usual AttachThreadInput/topmost toggle fallback.

### cross-platform-8. A data-directory override ending in a path separator makes replaced-database files undeletable and gives the instance a different pipe name

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Infrastructure/Sqlite/SqliteBackupFiles.cs:151`; effort S

**Evidence.** `AppPaths.Resolve` stores `Path.GetFullPath(overridden)` (AppPaths.cs:48), which keeps a trailing separator. `Delete(LeftoverDatabase)` requires `Path.GetDirectoryName(file)` to equal `Path.GetFullPath(paths.DataDirectory)` with StringComparison.Ordinal (line 151); GetDirectoryName never returns a trailing separator, so with `MINDWEAVE_DATA_DIR=E:\mw-try\` or `/tmp/mw-try/` the comparison is always false and an ArgumentException is thrown. BackupDialogs.cs:359 catches it and reports the generic "N 项没能删除，文件可能正被占用或没有权限". `InstanceKey` (AppPaths.cs:28-36) hashes the same un-trimmed string, so `/tmp/mw-try` and `/tmp/mw-try/` produce different pipe names for one directory. No test uses a trailing separator.

**Failure scenario.** A tester sets `$env:MINDWEAVE_DATA_DIR='E:\mw-try\'` (the checklist tells testers to set this variable), restores a backup from the recovery page (which always leaves `mindweave.db.replaced-*`), then tries to delete that entry in Settings › Data: it is refused every time with a misleading 'in use or no permission' message. A second launch with the other spelling waits 5 s and exits silently (see the start-up finding).

**Recommendation.** Normalise once in AppPaths.Resolve with Path.TrimEndingDirectorySeparator(Path.GetFullPath(...)), and compare directories with the same normalisation (case-insensitive on Windows and macOS). Add an AppPaths test for the trailing-separator and relative-path forms.

### cross-platform-9. Only answer and summary bodies pin a Simplified-Chinese font; all other Chinese UI text relies on per-locale system font fallback

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Desktop/App.axaml:18`; effort S

**Evidence.** `AnswerFontFamily` (PingFang SC, Microsoft YaHei UI, … $Default) is defined at App.axaml:18 and applied in exactly one place (Styles/Conversation.axaml:22). No default FontFamily or FontManagerOptions fallback list is configured anywhere (grep), so the sidebar, dialogs, settings, usage chart (UsageChart.cs:288) and network node labels (NetworkCanvas.cs:44, `new Typeface(FontFamily.Default)` with CurrentUICulture at line 735) take whatever CJK font the OS font fallback picks for the current UI culture. TRACK.md:154 records that fallback-drawn Chinese also carries the previous run's weight in Avalonia 12.1.4, and that the fix was applied to answer and summary bodies only.

**Failure scenario.** On a Windows or macOS account whose display language is not Simplified Chinese (English or Japanese UI, common for developers and on lab machines), system fallback can pick a Japanese or Traditional-Chinese face for Han characters. The all-Chinese chrome then shows different glyph shapes from the answer text in the same window, and any chrome text mixing bold and regular Chinese is exposed to the known weight-bleed bug. I did not observe this on a non-Chinese-locale machine.

**Recommendation.** Set the application-wide default family (or FontManagerOptions.FontFallbacks) to the same CJK-first list used for answers, and use it in NetworkCanvas and UsageChart instead of FontFamily.Default. Add one gallery scene rendered under an en-US UI culture.

### cross-platform-10. "Logging must never take the application down" holds only for IOException; an access-denied log file or pipe-name conflict faults instead

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Infrastructure/Logging/FileLoggerProvider.cs:63`; effort S

**Evidence.** `Write` catches only IOException (lines 59-66) around opening and writing the daily file; `Prune` (lines 85-94) catches only IOException around File.Delete and is called from the constructor (line 30), which runs while the service provider is built. On Windows, a read-only attribute or an ACL denial on a log file raises UnauthorizedAccessException, not IOException; on macOS/Linux a permission-denied open does the same. Similarly, SingleInstance.ListenAsync (SingleInstance.cs:162-169) catches only IOException: if creating the CurrentUserOnly pipe is denied (name already owned by another account on Windows), the listener task faults, and DisposeAsync (lines 116-125) rethrows it at exit before `_lock` is disposed.

**Failure scenario.** A log file from an earlier run is marked read-only or was created by an elevated run of the app. On the next normal start, Prune or the first log write throws UnauthorizedAccessException: from the constructor it aborts start-up with no window (see the start-up finding), and from a later write it propagates into whichever service logged, turning a diagnostics problem into a failed user operation.

**Recommendation.** Catch UnauthorizedAccessException (and SecurityException) alongside IOException in Write, WriterFor and Prune; in ListenAsync treat UnauthorizedAccessException as 'activation unavailable', log once and stop listening, so DisposeAsync never throws.

