# Hand-written native interop and OS integration code: credential stores, single-instance pipe, system shell, alert sound, app paths — grade B

The hand-written interop is small, careful and, as far as static reading can tell, correct: CF ownership is handled by a scope object with no leak or double release on the error paths I traced, the CREDENTIALW layout matches on x64/arm64, UTF-8/UTF-16 marshalling is consistent, the pipe protocol is one byte with a current-user-only server, credential references are unique GUID pairs so tracks/data directories cannot collide, and no test host or kill point reaches a published build. I found no critical or high defect in this dimension. What keeps it from an A is the behaviour around the native calls rather than the calls themselves: synchronous OS secret-store calls with no timeout or usable cancellation on every request, a single misleading user message for every OSStatus/Win32 failure, a 'key saved' indication that is never checked against the store, the Windows blob limit unhandled, unqualified helper executables, and a few classification/hardening gaps. None of the native code has been executed by me or (per already-reported findings) by any automated run, so correctness rests on reading alone.

**Strengths**

- MacKeychainCredentialStore.cs:142-180 CfScope owns every created CF object and releases once in Dispose; Own() throws on NULL before the pointer is used; the SecItemCopyMatching result is released in a finally (line 58-61) and is never touched when status is non-zero (Check at line 50 precedes the try). Dictionaries use kCFType callbacks so children are retained and the release order is safe. No leak or double release found.
- Constants are read correctly by symbol: Marshal.ReadIntPtr(GetExport(...)) for CFStringRef globals, but the raw export address for kCFTypeDictionaryKey/ValueCallBacks (lines 201-214) - the usual mistake of dereferencing the callbacks struct is avoided. CFIndex is declared nint (lines 130-139).
- WindowsCredentialStore.cs:113-128 Credential struct matches CREDENTIALW on 64-bit (FILETIME as long lands at offset 24, blob pointer at 40); CredFree is in a finally; CredWrite uses CRED_PERSIST_LOCAL_MACHINE so keys do not roam; the native plain-text copy is zeroed before FreeHGlobal (line 77).
- No plain-text fallback anywhere: Linux gets UnsupportedCredentialStore that throws (CredentialStores.cs:25-35), and Check()/Unavailable() never include the secret in exception text.
- Credential references are '{connectionId}/{Guid v7}' (ModelSettingsService.cs:93) under service 'Mindweave.{channel}', so two data directories or tracks cannot overwrite each other's items even though the namespace is per channel rather than per data directory.
- SingleInstance.cs: the server pipe is created with PipeOptions.CurrentUserOnly and maxInstances 1; exactly one byte is read and compared to a constant, the callback takes no data from the pipe, and App.axaml.cs:46 marshals it to the UI thread. The lock is an exclusively opened file, so a killed process leaves no stale lock.
- ISystemShell.cs:38-39 uses UseShellExecute=false with ArgumentList (no shell, no string concatenation), and the only two callers pass the app's own data directory (RecoveryViewModel.cs:78, StorageDetailsViewModel.cs:87) - no model- or user-content-derived path reaches Process.Start.
- Test hooks do not reach a published build: scripts/windows.sh:84 publishes only src/Mindweave.Desktop; Mindweave.TestHost is a separate test project with all kill points in its own Program.cs; AtomicFileWriter.Move is an internal init-only property (AtomicFileWriter.cs:24) and Motion.Enabled / ActivitySpinner.Animates are plain statics with no environment or command-line switch. The only environment variable the product reads is MINDWEAVE_DATA_DIR (AppPaths.cs:45).
- CopyButton.cs:57 catches the clipboard-busy exceptions Avalonia actually throws (COMException, TimeoutException) and shows the check only after a successful copy; SystemAlertSound.cs:25-33 contains DllNotFound/EntryPointNotFound so a missing sound API cannot fault the caller.

**Not examined**

- Nothing was executed: no build, no test run, no real Keychain or Credential Manager call. Statements about OS behaviour (keychain prompts after a rebuild, Win32 error code for an oversized blob, CreateProcess search order, .NET's Unix errno mapping, NamedPipeServerStream unlinking an existing socket) come from my knowledge of the platforms and the .NET runtime, not from observation in this session.
- Windows x86 / Arm64 behaviour of the Credential struct beyond layout reasoning (on x86 the managed struct is 4 bytes larger than native; not a supported target).
- Whether the advisory flock behind FileShare.None is honoured on network or exotic file systems for the instance lock.
- Avalonia's own platform interop (StorageProvider save dialog, clipboard backends, window activation) - only the app's calls into it were read.
- The baseline output files in the scratchpad (build/test/vuln/outdated/format) were not consulted; they are not relevant to this dimension.
- SqliteDatabase open/classification code other than the probe call site, FileLoggerProvider, and the restore file handling - covered by other dimensions.
- macOS packaging/signing implications for Keychain ACLs beyond what is already reported elsewhere.

### gap-native-interop-and-process-boundary-1. OS secret-store calls are synchronous with no timeout and no usable cancellation, and the key is re-read for every request

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Application/Conversations/GenerationRequests.cs:150`; effort M

**Evidence.** ReadKeyAsync calls `_credentials.ReadAsync(task.Model.CredentialRef, CancellationToken.None)`; it is called once per task by AnswerRunner.cs:54 and TaskRunners.cs:37/86/167/211, after the answer is already marked streaming (AnswerRunner.cs:51). The stores implement it as `Task.Run(() => Read(reference), cancellationToken)` (MacKeychainCredentialStore.cs:25-32, WindowsCredentialStore.cs:16-23): the token only prevents the start, the native SecItemCopyMatching / CredReadW call cannot be interrupted and has no timeout. Nothing caches the key for the session.

**Failure scenario.** On macOS the item's access list is tied to the binary that created it. After any rebuild or update of the (unsigned / ad-hoc signed) app, each SecItemCopyMatching raises the system 'wants to use your confidential information' password dialog and blocks its pool thread until the user answers. An answer, a title task and a summary started together each raise their own dialog. While a dialog is up the answer shows as generating, Stop has no effect (the stop token is not passed to the read), and the scheduler slot stays occupied. If the user is away, the slots stay blocked indefinitely. The same applies to Save/Test in settings (ModelSettingsService.cs:94, 234), which pass CancellationToken.None from the view model.

**Recommendation.** Read the key once per credential reference per session and hold it in memory (invalidate on Save); pass the task's stop token and race the read against it and a timeout so a blocked prompt turns into a CredentialUnavailable failure instead of an occupied slot; serialize store access so at most one OS prompt is open at a time.

**Skeptic: partially confirmed, low.** Every code fact in the finding holds, but the trigger is narrower than "any rebuild" and nothing shows it has occurred, so I rate it low rather than medium.

**What the code does (confirmed):**
- `ReadKeyAsync` passes `CancellationToken.None` to the store, and its signature takes no stop token.
- The answer runner calls it right after `live.MarkStreaming()` and `_events.Changed()`, so the answer already shows as generating while the key is read.
- Each of the four task runners calls it once per task; there is no cache anywhere. The only `ICredentialStore` consumers are `GenerationRequests` and `ModelSettingsService`.
- Both stores wrap the native call in `Task.Run(() => Read(...), cancellationToken)`. The token only prevents the start; there is no timeout and no lock serialising access.
- The macOS query sets only class, service, account, return-data and match-limit. It does not set `kSecUseAuthenticationUI` (fail instead of prompt) or `kSecUseDataProtectionKeychain`, so it uses the legacy file keychain with per-application access lists and interactive prompts allowed.
- Stop on a running answer cancels the token and then awaits `running.Completion`. During a blocked key read the cancel is unobserved, so Stop itself does not return until the dialog is answered.
- The scheduler has two slots (`_options.Slots`), so two blocked reads stall all generation.
- The settings view model passes `CancellationToken.None` to Save and Test.

I found no design decision or TRACK.md entry that accepts or mitigates this. The architecture only says an unavailable credential service must surface as unlock/retry, which a blocked prompt never reaches.

**Why the severity is overstated:**
- **Trigger frequency.** The documented way to run on macOS is `dotnet run`. The process identity the keychain sees is then the apphost stub, whose bytes do not normally change when only managed DLLs are rebuilt. A re-prompt is realistic after an SDK/apphost change, a single-file or bundled publish, or a move to a signed build, not after every rebuild. No macOS packaging exists in `src/` or `scripts/`; only the Windows publish command is documented.
- **One-time cost.** The system dialog offers "Always Allow", after which the new binary stops prompting.
- **Windows is unaffected.** `CredReadW` does not show UI. The known SSH-session failure returns error 1312 immediately and maps to `CredentialUnavailable`.
- **No data loss or security impact.** The outcome is a stalled answer or task until the user answers the dialog. The login keychain is normally unlocked for the whole session.
- **Unobserved.** The stage report records a live keychain read/write/delete as passing. I did not run anything to reproduce the prompt (read-only audit).

**What remains a real gap:** an OS call that can block indefinitely sits inside a scheduler slot with no stop token, no timeout and no fail-instead-of-prompt flag, and the user-facing Stop awaits it. The recommendation (pass the stop token and race it with a timeout, serialise prompts, optionally cache per session) is sound. Session caching is a trade-off, since it keeps the secret in memory longer, not a clear defect.

*Corrections to the finding.* - The file is `src/Mindweave.Application/Models/ModelSettingsService.cs`, not under `Settings/`.
- Line 94 of that file is a credential write (`WriteAsync` in Save), not a read; line 234 is the read used by Test and model listing. Writes to an existing item can also prompt, so the point stands with the wording corrected.
- "After any rebuild" is overstated. Under `dotnet run` the keychain identity is the apphost stub, which normally stays byte-identical when only managed code is rebuilt. Re-prompting is expected after an SDK/apphost change, a single-file or bundled publish, or a signing-identity change. This is platform reasoning, not something I reproduced.
- "Slots stay blocked indefinitely" needs the qualifier that this lasts only until the dialog is answered, and "Always Allow" removes it for that binary.
- "Stop has no effect" understates one detail: `StopAsync` also awaits `running.Completion`, so the Stop call itself hangs until the read returns.
- The scenario is macOS-only; the Windows store does not prompt.
- Severity: low, not medium — a real robustness gap, with a rare, self-clearing trigger and no data or security consequence.

### gap-native-interop-and-process-boundary-2. Settings always claims a key is saved; a credential reference with no item behind it is never detected until a request fails

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Desktop/ViewModels/Pages/ModelSettingsViewModel.cs:253`; effort S

**Evidence.** LoadAsync sets `HasSavedKey = true` whenever a settings row exists, without asking the store. ModelSettingsService.SaveAsync (line 88) reuses `existing.CredentialRef` when no new key is typed and never checks that the item exists. ConversationService.cs:560 gates sending only on `GetActiveSnapshotAsync() is null`. The first check is GenerationRequests.cs:151-152, inside the running task, which fails it with NotConfigured; the user text is "尚未配置模型或密钥" (UiText.Conversation.cs:102).

**Failure scenario.** The database carries a reference whose OS item is absent: the data directory was copied to another Mac/PC or user account, a backup from before a key change is restored (the old item was deleted by CleanUpRetiredCredentialsAsync), the keychain was reset, or the item was removed in Keychain Access / Credential Manager (Windows items are CRED_PERSIST_LOCAL_MACHINE and never travel). Settings shows the 'key saved' placeholder and Save succeeds with an empty key field; every question is accepted and persisted, then fails with 'model or key not configured' although the settings page says otherwise. The only way out is to guess that the key must be typed again.

**Recommendation.** When loading settings (and after a restore) probe the store for the active reference and show 'key missing on this computer - enter it again' when it returns null; reject the send up front with a specific rejection linking to settings; make SaveAsync fail with MissingKey when the kept reference has no item.

**Skeptic: partially confirmed, low.** Every cited code fact holds, and I found no guard, test or design decision that covers a reference whose OS item is absent. The severity is overstated because the triggers are rare, nothing is lost, and the failure text does name the key.

What holds:
- LoadAsync sets HasSavedKey = true for any settings row without asking the store, so the key field shows "已保存，留空则继续使用".
- SaveAsync reuses existing.CredentialRef when no key is typed and the service is unchanged. It throws MissingKey only when the reference is null, so Save succeeds and reports "已保存" over a dangling reference.
- CheckReadyAsync gates sending only on the active snapshot being null; it never reads the credential.
- The first read is ReadKeyAsync inside the running task, which fails the already-persisted task with NotConfigured.
- The restore trigger is real: CleanUpRetiredCredentialsAsync deletes replaced items once no unfinished task uses them, and an older database restored in place still points at the deleted reference.
- architecture.md:261 expects keys to be re-entered after moving machines, so a copied data directory is an anticipated path with no detection.

What is overstated:
- The failure text is "尚未配置模型或密钥" ("model or key not configured"). It names the key, so the user is not left purely guessing. It is still inaccurate (the model is configured) and contradicts the settings placeholder.
- The settings page does surface the problem on two paths: "测试连接" and "获取列表" go through KeyForAsync, which reads the store and throws MissingKey ("请填写 API 密钥。") when the item is absent.
- The workspace name-suggestion flow already handles a lost key with a "前往设置" link (s7-ui-polish.md:589). Summaries and titles I did not trace.
- No data is lost and nothing leaks. The question is kept, and typing the key again fully recovers.
- All triggers are uncommon manual or environmental events.

Against the requirement: requirements.md:385 asks that a new request "明确失败并提示解锁或重新填写" when the key is invalid. The current behaviour fails explicitly but only weakly prompts re-entry, and I found no settings link on the failed answer: the composer link is tied to SendRejection, not to the task's error kind (Composer.cs:53). I did not open the failed-answer view itself, so that last point rests on grep.

This is a real UX and diagnosability gap, best rated low to low-medium rather than medium. The recommended fixes (probe on load, MissingKey on save, a specific message) are reasonable.

*Corrections to the finding.* - "The only way out is to guess that the key must be typed again" is too strong. The failure text names the key ("尚未配置模型或密钥"), and "测试连接" or "获取列表" with an empty key field report "请填写 API 密钥。" because KeyForAsync reads the store.
- "Never detected until a request fails" is true for load, save and send, but not for the test and fetch buttons on the same page.
- The lost-key case is already handled with a "前往设置" link in the workspace name-suggestion flow; the gap is the settings page, SaveAsync and the conversation send path.
- The line reference is slightly off: the null check is GenerationRequests.cs:150-152, inside ReadKeyAsync.
- Severity should be low (low-medium at most): rare triggers, no data loss, full recovery by re-entering the key. The substantive defects are the false "saved" placeholder, Save succeeding over a dangling reference, and a weaker prompt than requirements.md:385 asks for.
- Not verified: that Windows items are CRED_PERSIST_LOCAL_MACHINE (my grep for it did not run cleanly). The finding does not depend on it.

### gap-native-interop-and-process-boundary-3. Every secret-store failure is reported as 'locked, unlock and retry' with a raw status number

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Infrastructure/Credentials/MacKeychainCredentialStore.cs:102`; effort S

**Evidence.** Check() turns any non-zero OSStatus into `CredentialUnavailableException($"Keychain {operation} failed (OSStatus {status}).")`; WindowsCredentialStore.Unavailable (line 95) does the same with the Win32 error. The UI has exactly two texts: "系统凭据服务不可用，请解锁后重试（{detail}）" (UiText.Settings.cs:94) and "系统凭据服务不可用（可能已锁定），请解锁后重试" (UiText.Conversation.cs:101). Only item-not-found (-25300 / 1168) and duplicate (-25299) are distinguished.

**Failure scenario.** User clicks Deny on the keychain dialog (errSecAuthFailed -25293 or userCanceled -128): told to unlock and retry, which repeats the same dialog. App started over SSH or without a GUI session (errSecInteractionNotAllowed -25308; Windows 1312, recorded in TRACK.md 2026-10-26): nothing can be unlocked. Missing entitlement after future signing/sandboxing (-34018), or an oversized key on Windows: same advice. On Linux the message is the English 'No supported credential store on this platform yet.' inside the same 'unlock' sentence. In each case the advice is wrong and the cause is only a number.

**Recommendation.** Carry a small reason enum on CredentialUnavailableException (Locked, Denied, NoInteractiveSession, Unsupported, TooLarge, Other) mapped from the known OSStatus/Win32 codes, with a distinct user text per reason; keep the raw code for the log.

**Skeptic: partially confirmed, low.** The cited code exists and behaves as described. MacKeychainCredentialStore.Check turns every non-zero OSStatus into one CredentialUnavailableException whose message carries only the number; WindowsCredentialStore.Unavailable does the same with the Win32 error. CredentialUnavailableException has no reason field, and there are exactly two user texts, both saying "unlock and retry". I found no guard, mapping or test that distinguishes denied, no-interactive-session or unsupported from locked.

The severity is overstated, for these reasons:

1. **The wording is what the spec asks for.** requirements.md:385 says to prompt "解锁或重新填写", and architecture.md:257 says "提示解锁／重试". The single-category design is deliberate (s1-chat.md:76; the code comment at MacKeychainCredentialStore.cs:106 lumps locked, denied and missing keychain together on purpose). The security-relevant property — never falling back to plain text — holds. This is a refinement gap in diagnostics, not a deviation from the design.

2. **The conversation text is already hedged.** It says "可能已锁定" (possibly locked), not that the store is locked.

3. **Most of the claimed scenarios are not realistic end-user paths.**
   - Linux is not a target platform (requirements: Windows + macOS; CredentialStores.cs:24 calls it "a later target").
   - Windows 1312 over SSH is a developer/test situation, already recorded in TRACK.md:124 as behaving as designed.
   - -34018 is hypothetical: there is no sandboxing or entitlement today.
   - An oversized secret on Windows would need an API key over about 2560 bytes.
   - That leaves the user clicking Deny on a macOS keychain prompt (or the prompt appearing after a binary signature change). It is real, but narrow, and the retry does re-present the dialog where the user can click Allow.

4. **Impact is limited to misleading advice plus an opaque number.** There is no data loss, no security consequence and no stuck state. The raw code is preserved in the message, the stored ErrorDetail and the logs, so it is diagnosable.

I did not run anything; this is from reading the code and docs. The recommendation (a reason enum with per-reason text) is reasonable polish. Because the wording is fixed by requirements.md and architecture.md, changing it needs a TRACK.md note and a user decision under the AGENTS.md rule.

*Corrections to the finding.* - The "unlock and retry" advice is not an implementation oversight: requirements.md:385 and architecture.md:257 prescribe that wording, so a fix needs a docs decision by the user, not just a code change.
- The Linux scenario is out of scope, since Linux is not a supported platform.
- The SSH/1312 scenario is a developer/test-only situation, already recorded in TRACK.md as intended behaviour.
- The -34018 and oversized-key scenarios are hypothetical.
- The conversation-side text is hedged with "可能已锁定", so it does not flatly assert that the store is locked.
- The realistic remaining case is a user denying a macOS keychain prompt, where the message is imprecise. That is low severity, not medium.

### gap-native-interop-and-process-boundary-4. Windows credential blob limit (2560 bytes) is not checked; a long key fails as 'credential service unavailable'

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Infrastructure/Credentials/WindowsCredentialStore.cs:65`; effort S

**Evidence.** Write() passes `CredentialBlobSize = bytes.Length` straight to CredWriteW. Windows limits generic credential blobs to CRED_MAX_CREDENTIAL_BLOB_SIZE = 5*512 bytes. No length check exists in WindowsCredentialStore, ModelSettingsService.SaveAsync (line 91-94 only trims) or the settings view model; the macOS store has no comparable limit, so behaviour differs by platform.

**Failure scenario.** A user of an OpenAI-compatible gateway that issues JWT-style bearer tokens longer than 2560 bytes saves the key on Windows: CredWriteW fails (I expect error 1783 or 87, not verified here), surfaced as "系统凭据服务不可用，请解锁后重试（Credential Manager write failed (Win32 error …)）". The same key saves fine on macOS. Nothing tells the user the key is too long.

**Recommendation.** Validate the UTF-8 length before the call and raise a specific 'key too long for this system's credential store' problem; add a test at the boundary (2560 and 2561 bytes) to the opt-in platform test.

### gap-native-interop-and-process-boundary-5. 'Open data folder' starts explorer.exe / open by bare name, so a same-named file in the working directory can be run instead

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Desktop/Services/ISystemShell.cs:37`; effort S

**Evidence.** `var opener = OperatingSystem.IsWindows() ? "explorer.exe" : OperatingSystem.IsMacOS() ? "open" : "xdg-open"; new ProcessStartInfo(opener) { UseShellExecute = false }`. With UseShellExecute=false Windows CreateProcess searches the application directory and then the current directory before the Windows directory where explorer.exe lives; .NET on Unix likewise checks the executable's directory and the current directory before PATH. The call is also unguarded: the two callers are plain synchronous RelayCommands (RecoveryViewModel.cs:78, StorageDetailsViewModel.cs:87).

**Failure scenario.** Mindweave.exe is started with a working directory the user does not control the contents of (a shortcut whose 'Start in' is Downloads or a shared folder, or a terminal in such a folder). A file named explorer.exe (or `open` on macOS) there is executed with the user's rights when they click 'open data folder' - including from the recovery page. Separately, if the opener cannot be started (xdg-open absent, policy block) Process.Start throws Win32Exception out of the command.

**Recommendation.** Use absolute paths: Path.Combine(Environment.GetFolderPath(SpecialFolder.Windows), "explorer.exe") and /usr/bin/open; catch Win32Exception/InvalidOperationException and show the path as text instead.

### gap-native-interop-and-process-boundary-6. Activation pipe: the client does not verify the server's owner, the listener retries IOException with no delay, and the pipe name is not derived from the resolved directory

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Infrastructure/Hosting/SingleInstance.cs:74`; effort S

**Evidence.** The client is `new NamedPipeClientStream(".", PipeNameFor(paths), PipeDirection.InOut, PipeOptions.Asynchronous)` - no PipeOptions.CurrentUserOnly, unlike the server (line 151). ListenAsync's `catch (IOException) { }` (line 166-169) loops straight back to constructing the server with no delay. AppPaths.InstanceKey (AppPaths.cs:28-36) hashes the upper-cased Path.GetFullPath string, which does not resolve symlinks and folds case even on case-sensitive volumes, while the lock is taken on the real file.

**Failure scenario.** (1) On a shared Windows machine another account creates \\.\pipe\mindweave-<hash> first (the hash is computable from the victim's default data path). The victim's second launch connects to it, receives 0x06 and exits 0 as 'activated' with no window; the victim's own listener cannot create its instance. (2) If creating the server instance keeps failing with an IOException (Windows 'all pipe instances are busy' when the name is held elsewhere), the loop spins a core for the life of the process. (3) MINDWEAVE_DATA_DIR=/tmp/x in one launch and /private/tmp/x in the next: same lock file, different pipe names, so the second launch waits 5 s and exits 2 on stderr instead of activating. Impact is limited to activation; no data is exposed.

**Recommendation.** Add PipeOptions.CurrentUserOnly to the client; back off (for example 250 ms, capped) after a failed server creation and log it; derive the key from the resolved real path of the directory (or store the pipe name in the lock file, which the second launch can read).

### gap-native-interop-and-process-boundary-7. Disk probe misclassifies a read-only volume and an exceeded quota on macOS/Linux as a generic I/O error

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Infrastructure/Sqlite/SqliteDiskProbe.cs:48`; effort S

**Evidence.** `IsDiskFull(ex) => (ex.HResult & 0xFFFF) is 0x70 or 0x27 || ex.HResult == 28` and only UnauthorizedAccessException maps to ReadOnly (line 23-26). On Unix .NET raises UnauthorizedAccessException only for EACCES/EPERM/EBADF; EROFS (30) and EDQUOT (69 on macOS, 122 on Linux) arrive as IOException with HResult = errno, so both fall to StorageFailureKind.IoError. The Windows-style mask is also applied to raw errno values on Unix (0x27 = 39, 0x70 = 112), which are unrelated errors there.

**Failure scenario.** The data directory is on a volume that has been remounted read-only (failing external disk, read-only disk image) or the user is over a disk quota: the storage notice reports an unspecified I/O error instead of 'read-only' or 'full', so the user gets the wrong recovery advice. The SQLite half classifies the same conditions correctly (StorageFailures.cs:13-16), so the two halves of one probe can disagree.

**Recommendation.** Branch on OperatingSystem: on Windows test the Win32 code from the HResult; on Unix compare HResult with ENOSPC and EDQUOT for Full and EROFS for ReadOnly. Add unit tests for the mapping function with synthetic IOExceptions.

### gap-native-interop-and-process-boundary-8. Plain-text key copies are cleared in one place only; the comment in the Windows store overstates it

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Infrastructure/Credentials/WindowsCredentialStore.cs:76`; effort S

**Evidence.** Windows Write zeroes the native blob ("Clear the plain-text copy before freeing it", line 76-77) but not the managed `bytes` array created on line 55, and Read leaves its `bytes` (line 43) uncleared. The macOS store clears nothing: `Encoding.UTF8.GetBytes(secret)` (line 68), the CFData built from it, and the read buffer (line 54) are all left for the GC/allocator. The key is also a managed string for the whole request path, so the single zeroing gives no real protection.

**Failure scenario.** A crash dump or memory capture taken after saving or using a key contains several copies of it, on both platforms. A reader of the Windows code would conclude from the comment that the plain text is cleared, which is not the case.

**Recommendation.** Either make it consistent (CryptographicOperations.ZeroMemory on the managed arrays in both stores, pinned for the duration of the call) or remove the half-measure and state in the design that the key lives in managed memory for the session. Do not write crash dumps with full heap by default.

### gap-native-interop-and-process-boundary-9. Data directory, database, backups and logs are created with default permissions; safe only because the default location sits under a private parent

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Infrastructure/Hosting/SingleInstance.cs:45`; effort S

**Evidence.** All four creation sites call `Directory.CreateDirectory(path)` with no mode (SingleInstance.cs:45, SqliteDatabase.cs:381, SqliteBackupFiles.cs:189, FileLoggerProvider.cs:29), giving 0755 directories and 0644 files under the usual umask. AppPaths.Resolve (AppPaths.cs:45-49) accepts any MINDWEAVE_DATA_DIR, including a relative one resolved against the current directory. On macOS the default ~/Library/Application Support is protected by ~/Library being 0700; on Linux ~/.local/share commonly is not.

**Failure scenario.** A user points MINDWEAVE_DATA_DIR at a shared or external location (/Users/Shared, a second disk), or runs on Linux with a world-traversable home: every conversation, backup and log is readable by any other local account. A relative override silently creates a second, empty data set depending on where the app was started from.

**Recommendation.** On Unix create the data, backup and log directories with UnixFileMode.UserRead|UserWrite|UserExecute (and tighten an existing directory the app owns); reject or warn about a relative MINDWEAVE_DATA_DIR.

### gap-native-interop-and-process-boundary-10. Observations: no window placement persistence exists, and debug and published builds of a track share one data directory and credential namespace

- Final: **info** (not verified); auditor said info
- Where: `src/Mindweave.Desktop/Views/MainWindow.axaml:9`; effort S

**Evidence.** MainWindow.axaml fixes `Width="1100" Height="720"` with `WindowStartupLocation="CenterScreen"`; MainWindow.axaml.cs has no code that saves or restores position, size or state, and no Screens check - so there is no off-screen-restore risk, and also no remembered size or maximized state. Separately, the channel is a single build property (`<MindweaveChannel>claude-auto</MindweaveChannel>`, Directory.Build.props:15) with no Debug/Release distinction; scripts/windows.sh:84 publishes with the same value.

**Failure scenario.** The window opens at 1100x720 centred on the primary screen on every launch regardless of how the user left it. A developer running a newer debug build on a machine that also has the published build migrates the real database forward, after which the older published build cannot open it; both also read and write the same OS credential items.

**Recommendation.** Decide whether remembering window size/state is wanted (if so, clamp to the current screens on restore). Consider a distinct channel suffix for Debug builds so development never touches release data or credentials.

