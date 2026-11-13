# Build, CI and release engineering — grade C

Build hygiene is good: warnings as errors, latest analyzers, central package versions, a clean build, no vulnerable packages, and narrowly scoped, justified suppressions. CI and release engineering fall well short of a production bar. CI has failed on every completed run since 2026-11-10 (12 in a row, same test) and four feature merges landed on red with no mention in TRACK.md. The primary platform (macOS) is never built or tested in CI, while unsupported Ubuntu is. There is no release pipeline: no macOS packaging, no signing, no CI-built artifact, and a constant version 0.1.0. `dotnet format --verify-no-changes` fails with 131 errors and nothing enforces it. Restore and SDK selection float, so builds are not reproducible. The cross-platform CI did catch real bugs early (CRLF, handle leaks, fonts), which keeps this at C rather than D.

**Strengths**

- Strict compiler baseline in Directory.Build.props: TreatWarningsAsErrors, AnalysisLevel=latest, AnalysisMode=Recommended, Nullable enabled. The baseline build (scratchpad build.txt) shows 0 warnings and 0 errors across 10 projects.
- Central package management (Directory.Packages.props) with exact versions and no floating ranges. vuln.txt reports no vulnerable packages; outdated.txt shows only patch/minor lag (Avalonia 12.1.4 vs 12.1.6, LiveMarkdown 2.5.1 vs 2.6.1) plus the 11.0.0 major of the Microsoft.Extensions packages.
- Suppressions are disciplined. There is no NoWarn in src. All 66 `#pragma warning disable` in src (64 CA1031, one CA1848, one CA1859) carry a one-line reason, and every file has matching disable/restore counts (checked by script). The only blanket NoWarn is in tests/Directory.Build.props, with a stated reason.
- CI has concurrency cancellation, a 20-minute timeout, fail-fast disabled, and a Windows leg that caught real platform bugs recorded in TRACK.md (CRLF changing raw string literals, a connection handle leak, crash-reopen misdetection). .gitattributes forces LF with the reason documented.
- scripts/windows.sh is careful: it retries only on ssh exit 255, verifies the synced file count against the expected count, strips AppleDouble metadata, and preserves the remote exit code.
- scripts/package-source.sh uses git archive, so only committed files ship, then checks size, forbidden file patterns and required files, and exits non-zero on failure.
- Per-track isolation is stamped at build time (MindweaveChannel assembly metadata, read by AppPaths.ChannelOf), and AppPaths throws if it is missing rather than silently sharing a data directory.

**Not examined**

- Did not run dotnet build, test, format or publish myself (read-only rule); relied on the scratchpad baseline outputs and GitHub Actions run history read through `gh run list/view`.
- Did not verify a self-contained win-x64 or osx-arm64 publish actually starts, nor measure publish size or startup time.
- Could not read branch protection settings (the API returns 403 for a private repo on the free plan), so 'no required checks' is inferred from merges landing on red.
- Did not confirm whether `gh workflow run ci.yml --ref track/claude-auto` can reach the macOS input from a non-default branch; I relied on the claim in the stress.yml header comment.
- Did not audit transitive package versions or native assets (SQLitePCLRaw, SkiaSharp, HarfBuzz) for vulnerabilities; vuln.txt appears to cover top-level packages only.
- Did not review scripts/mailbox.sh, mailbox.ps1 or real-api.sh in depth, nor the prototypes/ projects and their separate global.json files.
- Did not examine the Windows-side tester setup (E:\mindweave-wintest) or the stress workflow run history.
- Did not check the root causes of the 25 CI failures before 2026-11-10; only the last 12 were inspected for the failing test name.

### ci-build-1. CI has been red for 12 consecutive completed runs (since 2026-11-10) and is red at HEAD; feature merges landed on red

- Final: **high** (confirmed (one verifier lowered it)); auditor said high
- Where: `.github/workflows/ci.yml:44`; effort S

**Evidence.** `gh run list --branch track/claude-auto --workflow ci`: the last success is 8810ea22 on 2026-11-10T05:59Z. Every completed run since has failed (12 failures, 1 cancelled), including HEAD 1ba95c9 (run 43208608079: ubuntu-latest Test step failed, windows-latest passed). The same test fails each time: `SourceHighlightThemeTests.The_line_under_the_highlight_is_drawn_along_the_bottom_of_the_highlighted_text` (at HEAD: "only 4 of 54 pixels above the line are the tint #ff6e5210", SourceHighlightThemeTests.cs:79). Until 2026-11-12 it failed on both Windows and Ubuntu. The merges for items 3, 4, 4 follow-up, 5 and 6 were all pushed while it was red. Over the 94 runs retrieved: 35 success, 37 failure, 22 cancelled. TRACK.md line 23 reports only the macOS numbers ("macOS 892 通过"), and line 94 attributes discovery of the Windows failure to a manual report from the Windows tester, not to CI. Nothing in TRACK.md says CI is red.

**Failure scenario.** A change that breaks only Windows or only Linux (the class of bug TRACK.md records repeatedly: CRLF, handle leaks, timing) is pushed today. The run is red exactly as it was before, so nobody looks, and the regression ships in the next 'merged' checkpoint. Any second failing test on Ubuntu is currently masked by the standing failure.

**Recommendation.** Make the Ubuntu leg green now: fix the font-dependent assertion, or skip it on Linux with a stated reason, since Linux is not a supported platform. Then treat a red track branch as a stop condition in the track protocol, and record CI status per SHA in TRACK.md next to the macOS numbers. If the repo plan allows it, require the check before merging to the track branch.

**Skeptic: partially confirmed, medium.** I could not refute the core claim: CI on track/claude-auto has been red since 2026-11-10 and is red at HEAD, and nothing in TRACK.md says so. Two details are overstated, and the severity is one notch too high.

**What holds up**
- **Red streak:** `gh run list` returned 94 runs: 35 success, 37 failure, 22 cancelled, exactly as claimed. The last success is 8810ea22 (2026-11-10T05:59Z); every run after it failed or was cancelled.
- **HEAD is red:** run 43208608079 on 1ba95c9 has ubuntu-latest failed and windows-latest passed. The failing test and message match the claim ("only 4 of 54 pixels above the line are the tint #ff6e5210").
- **Same test throughout:** `SourceHighlightThemeTests.The_line_under_the_highlight_is_drawn_along_the_bottom_of_the_highlighted_text` fails in every run whose logs I read (6 of the 11).
- **Both platforms until 2026-11-12:** Windows and Ubuntu both failed from f0632e1 through 6389275. Windows went green at 4ca799c, the commit that reworked the test; that fix did not make Ubuntu pass.
- **Merges landed on red:** the "Track: item N merged" commits 89c85d9, f90da66, 6389275 and da4c804/1e6da26 all fall inside the red window.
- **Nobody was watching CI:** TRACK.md line 23 records only macOS counts. Line 94 credits the Windows tester's manual report on f90da66 for finding the failure, although Windows CI had been failing on that test since f0632e1, about two days earlier.
- **Masking has already happened once:** run 42800376085 (1ae0e83) also failed `ConversationNewsHintTests` and `WorkspaceNewsHintTests` on top of the standing failure.
- **No guard exi … (cut; full text in the workflow journal)

*Corrections to the finding.* - The failure count is 11 failures plus 1 cancelled since the last success, not "12 failures, 1 cancelled". "12 consecutive completed runs" is only accurate if the cancelled run c77d6b5 is counted.
- "The same test fails each time" is consistent with the 6 of 11 failing runs whose logs I read; the other 5 I checked only at job level. Run 42800376085 had two further failing tests on top of it.
- The item 3 merge is not identifiable as a distinct "Track:" commit in the log I inspected. Items 4, 4 follow-up, 5 and 6 are verified as landing on red; item 3 is dated 2026-11-10 in TRACK.md and plausibly did too, but I did not pin its commit.
- The recommendation to require the check before merging is not available on the current GitHub plan (branch protection returns 403 for this private repository).
- Severity is overstated. The currently red leg is Ubuntu, an unsupported platform, failing on … (cut; full text in the workflow journal)

**Reproduction attempt: confirmed, high.** This is a CI/process finding, so it cannot be shown by an xUnit test; I verified it by read-only inspection (gh run list / gh run view --log-failed, git log and git show on track/claude-auto). No build was run and no files in the worktree were changed.

Every factual claim checks out:
- The last green run is 8810ea22 (2026-11-10T05:59Z). The 13 runs since are 12 failures and 1 cancelled, including HEAD 1ba95c9 (run 43208608079: ubuntu-latest failed at Test, windows-latest passed).
- Totals over the 94 runs retrieved are 35 success, 37 failure, 22 cancelled, as claimed.
- The failing test at HEAD is SourceHighlightThemeTests.The_line_under_the_highlight_is_drawn_along_the_bottom_of_the_highlighted_text, with the message "only 4 of 54 pixels above the line are the tint #ff6e5210".
- The test arrived with merge 8d9bb8a (source-highlight) and was red from the very first run that included it (f0632e1), so it has never passed in CI on Ubuntu.
- Windows was red on the same test from 2026-11-10 until 6389275e (2026-11-12T04:28Z) and went green at 4ca799c7.
- The first-parent log shows the track commits for item 3 (5d5d55b), item 4 (89c85d9), item 4 follow-up (f90da66), item 5 (6389275) and item 6 (da4c804 / 1e6da26) all landed while CI was red.
- TRACK.md line 23 reports only macOS numbers ("macOS 892 通过"), and line 94 credits the Windows tester's first report on 2026-11-12 for finding the font-dependent test. CI had been showing that same Windows failure for two days.

The masking scenario is not hypothetical. During the red period two other tests failed in CI under the standing f … (cut; full text in the workflow journal)

*Corrections to the finding.* - "The same test fails each time" is slightly overstated. In 2 of the 12 failed runs (43034686900, 43058906488) the Ubuntu leg never reached the tests: it failed at the "CJK fonts (Linux)" apt-get step on a DNS error, and only the Windows leg failed on the SourceHighlight test. The test does fail in all 12 runs on at least one leg. The unretried apt-get install is a second, independent source of red.
- The finding understates the masking: two other tests (ConversationNewsHintTests on Windows twice, WorkspaceNewsHintTests on Ubuntu once) already failed under the standing failure on 2026-11-10.
- "Until 2026-11-12 it failed on both Windows and Ubuntu" is right; more precisely Windows was last red at 6389275e (04:28Z) and first green at 4ca799c7 (07:27Z).
- The last recommendation (require the check before merging) is not available today: branch protection returns 403 on the current GitHub … (cut; full text in the workflow journal)

<details><summary>Reproduction</summary>

Not reproducible by a local test, so none was written. This is a CI/process finding, and the failing assertion depends on the Ubuntu runner's CJK font (it passes on macOS and now on Windows). Verified by read-only commands instead:

$ gh run list --branch track/claude-auto --workflow ci --limit 100 (top rows)
2026-11-12T11:22:34Z 1ba95c9e completed failure 43208608079
2026-11-12T08:15:45Z 1e6da26f completed failure 43181493275
2026-11-12T08:07:47Z c77d6b50 completed cancelled 43180428727
2026-11-12T07:27:13Z 4ca799c7 completed failure 43175370696
2026-11-12T04:28:15Z 6389275e completed failure 43155006512
2026-11-11T16:29:56Z f90da664 completed failure 43058906488
2026-11-11T14:02:43Z 89c85d9e completed failure 43034686900
2026-11-10T15:04:56Z 7bd60bc8 completed failure 42840640782
2026-11-10T14:54:33Z 5d5d55b4 completed failure 42838876940
2026-11-10T10:35:01Z 1ae0e839 completed failure 42800376085
2026-11-10T08:36:24Z 5ed475fe completed failure 42783955522
2026-11-10T07:51:51Z f0632e18 completed failure 42778270780
2026-11-10T05:59:42Z 8810ea22 completed success 42765544033

Conclusion counts: [{"c":"cancelled","n":22},{"c":"failure","n":37},{"c":"success","n":35}]

$ gh run view 43208608079 --json jobs / --log-failed
test (ubuntu-latest) failure Test
test (windows-latest) success
[xUnit.net 00:02:23.37] Mindweave.Desktop.Tests.SourceHighlightThemeTests.The_line_under_the_highlight_is_drawn_along_the_bottom_of_the_highlighted_text [FAIL]
only 4 of 54 pixels above the line a … (cut; full text in the workflow journal)

</details>

### ci-build-2. The primary platform (macOS) is never built or tested in CI; an unsupported one (Ubuntu) gates instead

- Final: **medium** (confirmed (one verifier lowered it)); auditor said high; already known in TRACK.md
- Where: `.github/workflows/ci.yml:24`; effort S

**Evidence.** The matrix is `fromJSON(inputs.macos && '["ubuntu-latest","windows-latest","macos-latest"]' || '["ubuntu-latest","windows-latest"]')`. `inputs.macos` exists only on workflow_dispatch, so push and pull_request runs never include macOS. `git ls-tree main` shows no .github directory on the default branch, and the header of stress.yml (lines 3-5) states "A dispatch-only workflow cannot be started from a non-default branch", so by the repo's own account the macOS option is not reachable from the UI. requirements.md §5.3 and architecture.md line 17 name macOS as the primary environment and Windows 11 x64 as secondary, with Linux as a later target. macOS coverage therefore depends entirely on one developer machine (SDK 10.0.402 per TRACK.md line 126), with a warm NuGet cache and a possibly dirty working tree.

**Failure scenario.** A commit compiles on the developer Mac only because of an untracked file or the local SDK feature band. A clean clone on another Mac (osx-arm64 or osx-x64) fails to build, or a macOS-only code path (Keychain P/Invoke, data path, single-instance pipe) regresses. No clean-checkout macOS build exists to notice, while minutes are spent on Ubuntu, which nobody ships.

**Recommendation.** Add macos-latest to the push matrix, at least for build plus the Infrastructure and Core tests, or run it on a nightly schedule to limit the 10x billing. Consider dropping Ubuntu to build-only. Remove or document the unreachable dispatch input.

**Skeptic: partially confirmed, low.** The facts are accurate, but the finding presents a recorded, user-authorized cost decision as an oversight and overstates the risk.

What holds:
- The matrix at ci.yml:24 includes macos-latest only when `inputs.macos` is true, and that input exists only on workflow_dispatch (ci.yml:8-13). Push and pull_request runs get Ubuntu and Windows only.
- `git ls-tree main` shows no .github directory on the default branch, and stress.yml:3-5 states that a dispatch-only workflow cannot be started from a non-default branch. By the repo's own account the macOS switch is not reachable from the UI. I did not test whether an API or `gh workflow run --ref` dispatch works for ci.yml (no network calls).
- architecture.md:17 names macOS as the primary development and use environment, Windows as secondary, and Linux as a later target.

What is overstated:
1. This is a documented decision, not a gap nobody noticed. TRACK.md:48 records the authorization of 2026-10-25: CI runs Linux and Windows only while the repository is private, and the user may make the repo public to get free minutes. The 10x macOS billing is noted in ci.yml:11.
2. macOS is not untested. TRACK.md:23 records a full macOS run after every merged item (latest: 892 passed, 76 skipped, 0 warnings), plus slow-sync desktop runs and the screenshot gallery on macOS. Keychain, data-path and single-instance code is exercised there on every merge. Only a clean-checkout macOS build is missing.
3. The "untracked file" scenario is largely mitigated. AGENTS.md and TRACK.md:30 show each task is built in a fresh `git worktree add` checkout, whi … (cut; full text in the workflow journal)

*Corrections to the finding.* - "Never built or tested in CI" is correct, but the implication that macOS coverage is weak is not: the full suite runs on macOS at every merge (TRACK.md:23). Only a clean-checkout macOS run is missing.
- The omission is a recorded cost decision authorized by the user (TRACK.md:48), which the finding does not mention.
- "Possibly dirty working tree" is overstated: tasks are built in fresh git worktrees, and a missing tracked file would fail the clean Ubuntu and Windows CI builds.
- The SDK feature-band risk was already addressed (TRACK.md:126).
- Ubuntu is not an unsupported platform with no value: Linux is a stated later target, it is the cheapest runner, and it found a real font defect (TRACK.md:141).
- "Not reachable from the UI" rests on the repo's own comment in stress.yml; API or CLI dispatch with a ref was not tested.
- Severity should be low, not high.

**Reproduction attempt: confirmed, medium.** Not reproducible by an xUnit test (CI configuration), so I verified it by reading the workflow files from `track/claude-auto` and querying GitHub Actions history read-only with `gh`.

**Core claim holds.** No CI run has ever included a macOS job.
- `ci.yml` line 24 selects the matrix from `inputs.macos`, which is defined only under `workflow_dispatch` (lines 8-13). On `push` and `pull_request` the expression yields `["ubuntu-latest","windows-latest"]`.
- Run history: 94 `ci` runs, 92 from push and 2 from `workflow_dispatch`. One dispatch run was cancelled with no jobs; the other ran only Ubuntu and Windows. Both were on 2026-10-27.
- The docs name macOS as the primary development and usage environment, Windows 11 x64 as secondary, and Linux as a later target (`docs/architecture.md` section 1; `docs/requirements.md` line 118).
- macOS evidence in `TRACK.md` is local runs only ("macOS 892 通过、76 跳过").

**The claimed inversion is happening right now.** Ubuntu, the unsupported platform, is the only thing turning CI red.
- The last 12 `ci` runs on `track/claude-auto` (2026-11-10 to 2026-11-12) are failed or cancelled.
- In the latest run (43208608079), Windows succeeded and Ubuntu failed on one Desktop test: `SourceHighlightThemeTests.The_line_under_the_highlight_is_drawn_along_the_bottom_of_the_highlighted_text` (1 failed, 380 passed).
- I inspected the jobs of the latest run only; the earlier failures were not broken down by platform.

**Why medium, not high.**
- Omitting macOS is a recorded, deliberate cost decision, not an oversight. `TRACK.md` line 48 says CI runs only Linux … (cut; full text in the workflow journal)

*Corrections to the finding.* 1. **"The macOS option is not reachable" is overstated.** Two `workflow_dispatch` runs of `ci` exist on `track/claude-auto` (2026-10-27), so dispatch against the non-default branch works, presumably via `gh workflow run --ref` or the API. Because `ci.yml` also has push triggers, GitHub registered the workflow (`gh workflow list` shows `ci` as active). The input is reachable but has never been used with `macos=true`. Whether the web UI shows a "Run workflow" button was not checked.

2. **The omission is a documented decision, not an oversight.** `TRACK.md` line 48 records that CI runs only Linux and Windows while the repo is private, for cost reasons. The finding should be framed as an accepted risk worth revisiting.

3. **The "dirty working tree" scenario is weaker than stated.** Ubuntu and Windows clean-checkout builds with warnings as errors would catch untracked-file and most SDK-band … (cut; full text in the workflow journal)

<details><summary>Reproduction</summary>

No xUnit test applies; this is CI configuration. Verified with read-only commands run from the isolated worktree:

```
$ git show track/claude-auto:.github/workflows/ci.yml   (line 24)
os: ${{ fromJSON(inputs.macos && '["ubuntu-latest","windows-latest","macos-latest"]' || '["ubuntu-latest","windows-latest"]') }}

$ gh run list --workflow ci --limit 300 --json event --jq 'group_by(.event)|map({e:.[0].event,n:length})'
[{"e":"push","n":92},{"e":"workflow_dispatch","n":2}]

$ gh run view 40515538107 --json jobs,conclusion,headSha ...
b8b0654  success    test (ubuntu-latest)=success test (windows-latest)=success
$ gh run view 40515528722 --json jobs,conclusion,headSha ...
b8b0654  cancelled

$ gh api repos/<owner>/mindweave/actions/runs/43208608079/jobs --jq '.jobs[].labels'
["ubuntu-latest"]
["windows-latest"]

$ gh run view 43208608079 --json jobs ...
test (ubuntu-latest)   failure   Test
test (windows-latest)  success

$ gh run view 43208608079 --log-failed | grep ...
Mindweave.Desktop.Tests.SourceHighlightThemeTests.The_line_under_the_highlight_is_drawn_along_the_bottom_of_the_highlighted_text [FAIL]
Failed!  - Failed: 1, Passed: 380, Skipped: 58, Total: 439, Duration: 2 m 37 s - Mindweave.Desktop.Tests.dll (net10.0)

$ git ls-tree main --name-only
.gitignore .keep AGENTS.md CLAUDE.md README.md docs prototypes
```

No macOS job appears in any run. `git status --short` is empty.

</details>

### ci-build-3. No release pipeline: no macOS package, no signing, no CI-built artifact, version fixed at 0.1.0

- Final: **medium** (confirmed (one verifier lowered it)); auditor said high
- Where: `Directory.Build.props:17`; effort L

**Evidence.** `<Version>0.1.0</Version>` is the only version source. CompositionRoot.cs:35 strips everything after '+' from InformationalVersion, so every build identifies itself as 0.1.0. The only publish path is scripts/windows.sh line 84: `dotnet publish src/Mindweave.Desktop -c Release -r win-x64 --self-contained -o $DIR/$APP`, run after `sync`, which copies the working tree including untracked and uncommitted files, with no .git on the far side. docs/design/run-guide.md has a "Windows 发布" section and no macOS equivalent. A grep of src, scripts and docs/design finds no Info.plist, CFBundle, .icns, ApplicationIcon, codesign or notarization. Mindweave.Desktop.csproj sets no icon, RuntimeIdentifiers, PublishSingleFile or ReadyToRun. app.manifest declares assemblyIdentity version 1.0.0.0. Neither workflow has a publish or upload-artifact step.

**Failure scenario.** A user reports a bug from a published Mindweave.exe. The binary says 0.1.0, as every build since the start has, and may have been built from a dirty tree, so the commit cannot be identified. On macOS there is no distributable at all: the app runs only through `dotnet run` with an SDK installed, and an unsigned, un-notarized bundle would be blocked by Gatekeeper on any other Mac. On Windows the unsigned exe triggers SmartScreen.

**Recommendation.** Add a CI publish job per RID (win-x64, osx-arm64, osx-x64) that builds from a clean checkout, stamps the version from a tag or commit count plus SHA, keeps the SHA visible in the app's storage/about details, and uploads artifacts. Add a macOS .app bundle (Info.plist, icon) and decide on signing and notarization. Have windows.sh publish refuse a dirty tree or stamp '+dirty'.

**Skeptic: partially confirmed, medium.** Every factual claim checks out, but the severity is overstated: this is a missing capability that the authoritative docs do not require, not a defect in something shipped.

**What I confirmed**
- The version is fixed at 0.1.0 in one place, and the app deliberately strips the `+<sha>` suffix before showing it in Settings. Even a Mac build made inside the git checkout, where the SDK would add the commit, loses it.
- The Windows publish path syncs the working tree (tracked plus untracked, non-ignored files) and then publishes. There is no dirty-tree guard and no `.git` on the far side, so no revision can be embedded.
- The Desktop project sets no icon, RuntimeIdentifiers, PublishSingleFile or ReadyToRun, and the manifest says 1.0.0.0, which disagrees with 0.1.0.
- Neither CI workflow publishes or uploads an artifact, and macOS CI is opt-in.
- The run guide has a Windows publish section and no macOS equivalent. Info.plist and codesign appear only in the prototypes' packaging scripts, not in the product.
- No tags exist beyond `baseline-v0.11`.

**Why high is too strong**
- **No requirement is violated.** `docs/requirements.md` asks for no installer, signing or distribution; it names macOS as the main development and use environment and Windows as the course demo environment. `docs/architecture.md:463` lists the release target as still to be settled.
- **The failure scenario needs users who do not exist yet.** The published exe is used only by the developer for the manual Windows checklist, so "a user reports a bug from a published exe" is hypothetical today.
- **The course deli … (cut; full text in the workflow journal)

*Corrections to the finding.* - Severity should be medium, not high. No requirement calls for distribution, signing or installers, and the release target is an open architecture item.
- "On macOS there is no distributable at all: the app runs only through `dotnet run` with an SDK installed" is overstated. A self-contained `dotnet publish -r osx-arm64` folder would run without an SDK; what is missing is a documented path, a `.app` bundle, an icon and signing.
- "The commit cannot be identified" is slightly overstated. The sync step prints `synced <sha>[+dirty]` to the operator's console (`scripts/windows.sh:71`) and stage reports record publishes. It is true that nothing is stamped into the binary.
- The SHA is lost twice over, which the finding only half states: the Windows snapshot has no `.git`, and `CompositionRoot.cs:35` strips the suffix even where the SDK would supply it.
- The Gatekeeper and SmartScreen conseq … (cut; full text in the workflow journal)

**Reproduction attempt: confirmed, medium.** This is a process/release-engineering finding, not a code defect, so I verified it by read-only inspection of track/claude-auto (1ba95c9) rather than by a test. No build or test was run.

Every factual claim checks out:
- Version: Directory.Build.props:17 `<Version>0.1.0</Version>` is the only version source. There is no MinVer/GitVersion, no SourceRevisionId handling and no version tags in use.
- Commit identity is discarded: CompositionRoot.cs:35 takes `InformationalVersion.Split('+')[0]`, so the SHA suffix the SDK adds on a local git build never reaches AppEnvironment or the settings page (SettingsHomeView.axaml:197 `AppVersion`).
- The published Windows build has no SHA at all: `scripts/windows.sh publish` runs `sync` first, which tars `git ls-files --cached --others --exclude-standard` (tracked plus untracked, uncommitted edits included) into `src-snapshot` with no .git, then runs `dotnet publish ... -r win-x64 --self-contained` (line 84).
- No macOS distributable: run-guide.md has only `dotnet run` for running and a "Windows 发布" section. A grep of the whole track tree (excluding reviews/history/prototypes) for Info.plist, CFBundle, .icns, ApplicationIcon, codesign, notari*, signtool, PublishSingleFile, ReadyToRun, RuntimeIdentifier returns nothing. There is no Assets directory under src/Mindweave.Desktop.
- Mindweave.Desktop.csproj sets only OutputType, AssemblyName, RootNamespace, compiled bindings and ApplicationManifest. app.manifest declares assemblyIdentity version 1.0.0.0, which disagrees with 0.1.0.
- CI: ci.yml does restore/build/test only; stress.yml does bui … (cut; full text in the workflow journal)

*Corrections to the finding.* 1. "may have been built from a dirty tree, so the commit cannot be identified" is slightly overstated: windows.sh:73 does print `synced <short-sha>[+dirty]` to the operator's console at sync time. The gap is that nothing is stamped into the binary or the publish folder, so the information is lost once the terminal scrolls away.
2. "CompositionRoot strips everything after '+' ... so every build identifies itself as 0.1.0" is accurate, but for the Windows publish path the strip is not even the cause: the snapshot has no .git, so the SDK has no SHA to append in the first place. Removing the Split alone would not fix published builds; the SHA must be passed in (e.g. -p:SourceRevisionId) or the build must run from a real checkout.
3. The Gatekeeper and SmartScreen consequences are inferences from the absence of any signing configuration, not something observed; there is no bundle to test. The … (cut; full text in the workflow journal)

<details><summary>Reproduction</summary>

Not reproducible by a unit test (release process / CI / packaging gap); verified by read-only inspection only. No test was written, nothing was built, and the worktree was left unmodified (no files added or changed, nothing to restore).

</details>

### ci-build-4. `dotnet format --verify-no-changes` fails with 131 errors; no .editorconfig exists and CI never checks formatting

- Final: **low** (confirmed in part); auditor said medium
- Where: `Directory.Build.props:10`; effort S

**Evidence.** The baseline format.txt ends with "format exit 2" after 131 WHITESPACE errors: 127 in tests/Mindweave.TestHost/Program.cs (from line 32, the bodies of braced `case` blocks) and 4 in src/Mindweave.Application/Models/ModelSettingsService.cs:118-122. `find . -name .editorconfig` returns nothing, and there is no .globalconfig. `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>` therefore enforces nothing in practice, because IDE-rule severities default to suggestion or silent without an editorconfig. ci.yml has only Restore, Build and Test steps.

**Failure scenario.** Code style is whatever each agent session or IDE produces. Two parallel worktrees (the AGENTS.md workflow) reformat the same file differently and create avoidable merge conflicts. Anyone who runs `dotnet format` before committing rewrites 131 lines unrelated to their change.

**Recommendation.** Commit an .editorconfig that encodes the style actually in use, including the switch-case block indentation the code prefers and severities for the IDE rules that should fail the build. Fix or exempt the two files, and add `dotnet format --verify-no-changes --no-restore` to CI.

**Skeptic: partially confirmed, low.** Every factual claim checks out, but the impact is overstated, so this is low rather than medium.

**What I confirmed**
- The baseline format.txt ends with "format exit 2" and contains exactly 131 WHITESPACE errors: 127 in tests/Mindweave.TestHost/Program.cs and 4 in src/Mindweave.Application/Models/ModelSettingsService.cs (lines 118, 119, 120, 122).
- No .editorconfig or .globalconfig exists anywhere in the worktree; the repo root has only .gitattributes and .gitignore.
- Directory.Build.props:10 sets `EnforceCodeStyleInBuild` to true. With no editorconfig, IDE rules stay at their default suggestion/silent severity, so the setting has essentially nothing to fail the build on, even with `TreatWarningsAsErrors`.
- ci.yml has only Restore, Build and Test steps (plus a font install); stress.yml has no format step either.
- I found no mention of `dotnet format` or editorconfig in TRACK.md or docs/design, so this is neither a known finding nor a deliberate decision.

**Why the severity drops**
- Only 2 files out of roughly 69k lines deviate from the dotnet-format defaults. The codebase is in practice almost fully consistent, so "style is whatever each session produces" is not what the evidence shows.
- The 127 TestHost errors are one stylistic choice: bodies of braced `case` blocks are not given the extra indent that the default `csharp_indent_case_contents_when_block = true` expects. The 4 ModelSettingsService errors are one multi-line conditional expression with a `with` clause.
- The merge-conflict scenario between parallel worktrees is speculative. Nothing in the repo tells a … (cut; full text in the workflow journal)

*Corrections to the finding.* - Severity should be low, not medium: the measured drift is two files, and the merge-conflict scenario is hypothetical.
- "Enforces nothing in practice" is slightly absolute. It is accurate that default IDE-rule severities do not surface as build warnings without an editorconfig, but `AnalysisMode=Recommended` with `TreatWarningsAsErrors` does still enforce CA rules; only the IDE/code-style half is inert.
- "Anyone who runs dotnet format rewrites 131 lines" is true but confined to two files: one switch statement in the test host and one expression in ModelSettingsService.

### ci-build-5. The Keychain and Windows Credential Manager implementations are never exercised by any automated run

- Final: **low** (confirmed in part); auditor said medium
- Where: `tests/Mindweave.Infrastructure.Tests/Chat/PlatformCredentialStoreTests.cs:14`; effort S

**Evidence.** The only test of the real stores begins with `Assert.SkipUnless(Environment.GetEnvironmentVariable("MINDWEAVE_CREDENTIAL_TESTS") == "1", ...)`. Grepping .github and scripts for MINDWEAVE_CREDENTIAL_TESTS finds no place that sets it; it appears only in this test and in docs. The code under test is hand-written source-generated P/Invoke (Mindweave.Infrastructure.csproj sets `AllowUnsafeBlocks` for "Keychain and Windows Credential Manager"; MacKeychainCredentialStore.cs, WindowsCredentialStore.cs). CI has no macOS leg, and the Windows leg skips the test. Hosted Windows runners have a usable Credential Manager, and the test already uses a throwaway namespace and deletes what it creates.

**Failure scenario.** A refactor changes a marshalling attribute or a buffer length in WindowsCredentialStore, or an SDK update changes LibraryImport code generation. All tests stay green because every other test uses the in-memory store. The first symptom is a user whose API key can no longer be saved or read back, or is truncated for non-ASCII keys.

**Recommendation.** Set MINDWEAVE_CREDENTIAL_TESTS=1 on the windows-latest leg, and on a macOS leg once one exists (the runner keychain is unlocked for the job). Keep it opt-in locally.

**Skeptic: partially confirmed, low.** The core claim holds: no automated run sets MINDWEAVE_CREDENTIAL_TESTS, so the only test that touches the real Keychain / Credential Manager is skipped on every CI leg. Everything else uses substitutes.

What I could not refute:
- The test's first statement is the opt-in skip, exactly as quoted.
- A repo-wide grep finds the variable only in that test file and in one TRACK.md note. Neither workflow nor any script sets it.
- The stores are hand-written LibraryImport P/Invoke with a manually laid-out CREDENTIAL struct and manual blob marshalling; a layout or marshalling slip would not be caught by any always-on test.
- No other test references the platform stores, apart from an architecture seam test.

Why medium is overstated:
- The gap is a known, deliberate choice, not an oversight. The test's doc comment gives the reason for the opt-in (it writes to the signed-in user's secret store), and TRACK.md's 2026-11-12 entry records that it is opt-in rather than platform-gated.
- Manual coverage exists. Stage reports record a measured macOS Keychain read/write/delete and a user-confirmed Windows Credential Manager save on 2026-10-26. The Windows acceptance checklist row 24 lists this test for desktop-session runs, and the Windows tester can run it there with the user's consent.
- The code is 129 lines (Windows) and 217 lines (macOS) and rarely changes.
- If it did break, the product raises CredentialUnavailableException and fails the request explicitly rather than falling back to plaintext. The outcome is a loud functional failure, not a silent security regression.
- The "truncated … (cut; full text in the workflow journal)

*Corrections to the finding.* - "CI has no macOS leg" is inaccurate. ci.yml has an opt-in macOS leg through the workflow_dispatch input `macos` (default false, off for push and PR). That leg would also skip the test, since the variable is not set.
- "Never exercised" is true only for automated runs. Manual and desktop-session runs are documented (stage-reports.md, s6-windows-checklist.md row 24, TRACK.md 2026-11-12).
- "Hosted Windows runners have a usable Credential Manager" is asserted without evidence. TRACK.md records error 1312 (ERROR_NO_SUCH_LOGON_SESSION) under an SSH logon, so runner behaviour needs to be tried first.
- Severity should be low: this is a regression-detection gap on small, stable code, mitigated by manual checks and a fail-loud design.

### ci-build-6. CI verifies only the track branch after the merge, cancels intermediate commits, and keeps no diagnostics from failures

- Final: **low** (confirmed in part); auditor said medium
- Where: `.github/workflows/ci.yml:4`; effort S

**Evidence.** Triggers are `push: branches: [track/claude-auto]` and `pull_request: branches: [track/claude-auto]`. The AGENTS.md workflow develops on task branches and worktrees (for example `worktree-wf_96b023d6-d60-1`) and merges locally with no PR, so the first CI signal comes after the merge is already on the track branch. `concurrency: cancel-in-progress: true` cancelled 22 of the 94 runs retrieved, so those SHAs were never verified. The Test step is a bare `dotnet test Mindweave.slnx --no-build -c Release`: no trx logger, no `--blame-hang`, no upload-artifact. TRACK.md lines 105-106 and 145 document intermittent UI-test timeouts and load-only failures, which are exactly the failures that need logs or dumps.

**Failure scenario.** Three merges are pushed within ten minutes. The first two runs are cancelled and the third fails. There is no record of which merge introduced the failure and no artifact beyond console text. An intermittent hang hits the 20-minute job timeout and leaves nothing to diagnose.

**Recommendation.** Also trigger on task-branch pushes (for example `claude-auto/**` and `worktree-*`), or require a PR into the track branch. Add `--logger trx --blame-hang-timeout 5m` and upload TestResults on failure. Keep cancel-in-progress for task branches only.

**Skeptic: partially confirmed, low.** Every factual claim checks out, but the impact is overstated for this repository, so I put it at low rather than medium.

**What is true**
- CI triggers only on push and pull_request to `track/claude-auto`, plus manual dispatch.
- `concurrency: cancel-in-progress: true` is keyed on the ref, so a newer push cancels the run for the older one.
- The Test step is a bare `dotnet test Mindweave.slnx --no-build -c Release`. There is no trx logger, no `--blame-hang`, and no upload-artifact anywhere in `.github` or `scripts`.
- The last 94 runs of `ci.yml` include 22 cancelled (21 push, 1 dispatch). All 92 non-dispatch runs are push events on the track branch; there are no pull_request runs.
- The latest run has 0 artifacts.
- Merges are local with no PR (git log shows `Merge ...` commits and local `worktree-wf_*` branches).

**Why medium is too high**
1. Console output is enough to diagnose an ordinary failure. The latest failed run's log names the failing test (`SourceHighlightThemeTests.The_line_under_the_highlight_is_drawn_along_the_bottom_of_the_highlighted_text`) with its error message. "No diagnostics" is really "no trx and no hang dump".
2. CI is not the pre-merge gate by design. AGENTS.md requires merging the track branch into the task branch and running all tests before merging back. TRACK.md records local macOS full runs and Windows real-machine runs per stage.
3. A cancelled run means an intermediate SHA was skipped, not that the code went unverified: the newest SHA in the group still runs. Attribution across two or three single-author merges is cheap to redo locally.
4. … (cut; full text in the workflow journal)

*Corrections to the finding.* - "Keeps no diagnostics from failures" is overstated. Console logs are retained and include failing test names and error messages; what is missing is trx files, hang dumps and uploaded TestResults.
- TRACK.md lines 105-106 and 145 describe issues that were root-caused and fixed (2026-11-05 and 2026-10-28). They are not open intermittent hangs, and none was a 20-minute job timeout.
- The 22 cancelled runs break down as 21 push runs and 1 workflow_dispatch run.
- "Those SHAs were never verified" holds for hosted CI only. The repo's process requires a local full test run before each merge, and the superseding SHA is still run.
- Severity should be low, not medium: this is a diagnosability and cost trade-off in a single-developer private repo.
- Omitted by the finding and more important: the last 12 completed CI runs on the track branch are all red, with a Linux-only Desktop test failure on … (cut; full text in the workflow journal)

### ci-build-7. Builds are not reproducible: the SDK floats across feature bands with AnalysisLevel=latest, there are no NuGet lock files, and no package source is pinned

- Final: **low** (confirmed in part); auditor said medium; already known in TRACK.md
- Where: `global.json:4`; effort M

**Evidence.** global.json: `"version": "10.0.100", "rollForward": "latestFeature"`. ci.yml line 35: `dotnet-version: 10.0.x`. Directory.Build.props: `AnalysisLevel` latest, `AnalysisMode` Recommended, `TreatWarningsAsErrors` true, `LangVersion` latest. TRACK.md line 126 records three different SDKs in use (macOS 10.0.402, Windows 10.0.103, CI newest). No packages.lock.json exists anywhere, `RestorePackagesWithLockFile` is not set, and there is no nuget.config (so no packageSourceMapping and reliance on machine-level sources). Transitive native dependencies (SQLitePCLRaw, SkiaSharp, HarfBuzzSharp) are unpinned. With warnings as errors, a NuGet audit advisory (NU190x) would also become a build error.

**Failure scenario.** A new .NET 10 feature band ships with an added or stricter CA rule. The same commit that was green yesterday fails on CI, or passes on the 10.0.103 Windows laptop and fails on the 10.0.402 Mac. Separately, a clean restore weeks later can resolve different transitive packages than the build that was tested, with nothing in the repo to detect it. A course or customer rebuild of a tagged commit cannot be guaranteed to match.

**Recommendation.** Enable RestorePackagesWithLockFile and commit the lock files, then restore with `--locked-mode` in CI. Add a nuget.config with a single source and source mapping. Pin AnalysisLevel to a number (10.0) instead of latest. Pin CI to one feature band, or log `dotnet --version` and accept the float knowingly. Downgrade NU190x to warnings and report them in a separate scheduled audit job.

**Skeptic: partially confirmed, low.** Every cited fact checks out, but the headline risk is overstated and the largest part of it is a recorded, deliberate decision.

**What is true**
- global.json pins 10.0.100 with rollForward latestFeature; both workflows install 10.0.x.
- Directory.Build.props sets AnalysisLevel latest, AnalysisMode Recommended, LangVersion latest, TreatWarningsAsErrors true and EnforceCodeStyleInBuild true.
- No packages.lock.json and no nuget.config are tracked, and RestorePackagesWithLockFile is not set anywhere.
- Nothing sets NuGetAudit, WarningsNotAsErrors or any NU190x exemption, so a newly published advisory would fail restore and turn a previously green commit red.
- The analyzer drift scenario is real: a newer feature band can add or tighten CA/IDE rules under AnalysisLevel=latest, and the three environments (Mac 10.0.402, Windows 10.0.103, CI newest) can disagree on the same commit.
- TRACK.md line 130 shows float has already bitten once: a CI runtime move from 10.0.13 to 10.0.14 made a latent timing bug reproduce every run.

**What is overstated**
- The SDK float is intentional, not an oversight. TRACK.md line 126 records that the earlier pin (10.0.402 + latestPatch) stopped the Windows machine from building, so it was changed to 10.0.100 + latestFeature. The CI comment and docs/design/run-guide.md line 7 say the same. The real defect is narrower: AnalysisLevel=latest plus warnings-as-errors on top of an accepted float, and CI not logging the SDK version.
- "A clean restore weeks later can resolve different transitive packages" is mostly wrong for this repo. Central package mana … (cut; full text in the workflow journal)

*Corrections to the finding.* - The title's "builds are not reproducible" is too strong. Direct package versions are pinned exactly through central package management, transitive resolution is deterministic for a fixed source, and Deterministic=true is set. The real exposure is toolchain drift (SDK and analyzers), not package drift.
- The claim that a later clean restore "can resolve different transitive packages" should be dropped or narrowed. It needs a changed source set or a tampered or replaced package, since no floating versions exist here. Lock files add hash verification, not drift protection.
- The SDK float should be presented as a recorded decision (TRACK.md line 126, run-guide.md line 7, the ci.yml comment), not a gap. The actionable part is AnalysisLevel=latest with warnings-as-errors, and CI not recording the SDK version.
- "No package source is pinned" is accurate, but the practical risk is low: only n … (cut; full text in the workflow journal)

### ci-build-8. Workflow supply-chain hardening is missing: mutable action tags, no permissions block, script injection in stress.yml, no dependency update automation

- Final: **low** (confirmed in part); auditor said medium
- Where: `.github/workflows/stress.yml:44`; effort S

**Evidence.** Both workflows use `actions/checkout@v4` and `actions/setup-dotnet@v4` (mutable tags, not commit SHAs), and neither declares `permissions:`, so GITHUB_TOKEN has the repository default scope. stress.yml interpolates dispatch inputs directly into shell text: `run: dotnet build ${{ inputs.project || ... }}` and, inside the pwsh script, `--filter "${{ inputs.filter || 'Category=FaultInjection' }}"` and `for (...; $i -lt ${{ inputs.runs || '4' }}; ...)`. The defaults also disagree: the input descriptions say runs "12" and workers "4", while the push fallbacks are '4' and '3'. There is no dependabot.yml or renovate config; outdated.txt shows Avalonia two patch releases behind and LiveMarkdown one minor behind.

**Failure scenario.** A retagged or compromised action runs with a token that may have write scope on a repo that auto-pushes. A filter value containing `"; <command>; "` executes arbitrary PowerShell on the runner (limited today to people with write access, since it needs dispatch). Patch releases that fix rendering bugs the team works around (TRACK.md line 154 names an Avalonia 12.1.4 font-fallback bug) go unnoticed.

**Recommendation.** Add `permissions: contents: read` at the top of both workflows. Pin actions to commit SHAs. Pass inputs through `env:` and reference `$env:FILTER` and similar in the script. Add dependabot for nuget and github-actions on the track branch.

**Skeptic: partially confirmed, low.** Every cited fact is real, but the exploit scenario is weaker than claimed and one recommendation would not work as written, so this is low-severity hygiene rather than medium.

What holds up:
- Both workflows use `actions/checkout@v4` and `actions/setup-dotnet@v4` by mutable tag.
- Neither workflow has a `permissions:` block.
- stress.yml splices `${{ inputs.* }}` straight into shell text in the Build step and the pwsh script.
- The dispatch defaults (runs 12, workers 4) differ from the push fallbacks (4 and 3), while the header comment says the push run uses "the defaults below".
- There is no dependabot or renovate config, and outdated.txt shows Avalonia 12.1.4 against 12.1.6 and LiveMarkdown 2.5.1 against 2.6.1.
- TRACK.md line 154 does name an Avalonia 12.1.4 fallback-font bug that the team works around.
- I found no decision in TRACK.md or docs/design that accepts these gaps.

What is overstated or wrong:
1. **The injection path is unreachable today.** `main` and `track/codex-steady` have no `.github` directory, so `workflow_dispatch` cannot be started; the file's own comment and commit 8f249a1 say so. On the push trigger `inputs.*` is empty and only the hard-coded literals are used. Even if dispatch became reachable it needs write access, and anyone with write access can already edit the workflow, so there is no privilege gain.
2. **The token-scope risk is speculative.** I could not check the repository's default GITHUB_TOKEN permission (no network or settings access); repositories created since February 2023 default to read-only. The workflows reference no secrets an … (cut; full text in the workflow journal)

*Corrections to the finding.* - Severity should be low, not medium.
- The script-injection path cannot be triggered today: `workflow_dispatch` needs the workflow on the default branch and `main` has no `.github` directory. Only the push trigger runs, with hard-coded literals. If dispatch became reachable it would still need write access, which already allows editing the workflow.
- "A token that may have write scope on a repo that auto-pushes" is unsupported. The default token scope was not verified, and the workflows use no secrets and publish nothing.
- "Add dependabot on the track branch" would not work. Dependabot reads its config only from the default branch, so it needs a file on `main` with `target-branch`, which under AGENTS.md is a user decision.
- Nothing shows that Avalonia 12.1.5 or 12.1.6 fixes the font bug in TRACK.md line 154.
- The injection sites are lines 41, 45, 50, 51 and 58, not just line 44. `ru … (cut; full text in the workflow journal)

### ci-build-9. The screenshot gallery (55 scenes x 2 themes) and all performance budgets never run in CI

- Final: **low** (confirmed in part); auditor said medium
- Where: `tests/Mindweave.Desktop.Tests/GalleryTests.cs:718`; effort S

**Evidence.** `Assert.SkipWhen(ScreenshotDir is null, "Set MINDWEAVE_SCREENSHOT_DIR to render the gallery.")`. test.txt and the CI logs show 58 skipped in Mindweave.Desktop.Tests, all gallery scenes G01 to G49b plus the performance tests. Performance tests are gated by `MINDWEAVE_PERF` (StoragePerformanceTests.cs:32, StatisticsPerformanceTests.cs:29, AnswerRenderPerformanceTests.cs:63/73/82) and run only through scripts/perf.sh by hand. ci.yml sets neither variable and uploads no artifacts. Only an unenforced `Smoke` test (AnswerRenderPerformanceTests.cs:51, `enforce: false`) runs. TRACK.md line 23 cites the gallery as release evidence ("截图库两种主题各 55 个场景"), produced manually on macOS only.

**Failure scenario.** A view-model or XAML change makes a gallery scene throw, or render blank on Windows fonts. CI stays green because the scene is skipped, and the breakage surfaces only when someone next regenerates screenshots on the Mac. A query change that triples conversation-open time at 1,000 conversations passes CI because no budget is ever enforced there.

**Recommendation.** In CI, set MINDWEAVE_SCREENSHOT_DIR to a temp folder on at least the Windows leg and upload the PNGs as an artifact; this also gives the user Windows screenshots without the laptop. Run scripts/perf.sh on a nightly or weekly schedule with budgets relaxed for hosted runners, or at least record the measured times.

**Skeptic: partially confirmed, low.** The factual core holds. Every gallery scene and every full-size performance test is opt-in by environment variable, and neither CI workflow sets those variables or uploads artifacts. I found no decision record that makes this a deliberate, documented CI exclusion; the code comments only justify the perf opt-in by run time.

What I confirmed:
- GalleryTests has 55 scene methods, each starting with a skip when MINDWEAVE_SCREENSHOT_DIR is unset.
- The five full-size perf tests (two in Infrastructure, three in Desktop) skip unless MINDWEAVE_PERF=1, and only scripts/perf.sh sets it.
- ci.yml runs a plain `dotnet test` on ubuntu and windows; stress.yml only repeats a fault-injection filter.
- The baseline test run shows 58 skipped in Mindweave.Desktop.Tests, which matches 55 gallery scenes plus 3 perf tests.
- The Smoke tests that do run in CI use `enforce: false` / `enforceBudgets: false`, so no timing budget is asserted there.

Why medium is overstated:
- The gallery is a screenshot generator for human review (s7-ui-polish.md:57 says each scene is looked at by hand), not a regression suite. The same views and view models are covered by 381 Desktop tests that pass in CI, including real-pointer UI tests, so "a XAML or view-model change breaks and CI stays green" applies only to scene-seeding code or paths no other test touches.
- "Renders blank on Windows fonts" would not be caught by the recommendation either: the gallery has no pixel comparison (about 70 asserts across 55 scenes), so running it in CI catches only thrown exceptions unless someone inspects the uploaded PNGs.
- Pe … (cut; full text in the workflow journal)

*Corrections to the finding.* - Severity: low, not medium. The gallery is a human-review screenshot generator with no image comparison; the pages it renders are covered by 381 Desktop tests that run in CI.
- "All performance budgets never run in CI" is accurate for budget enforcement, but the same code paths do run at smoke size in three Smoke tests on every CI run.
- The "renders blank on Windows fonts" scenario would not be detected by the proposed fix unless someone looks at the uploaded PNGs, since no scene asserts on pixels.
- "CI stays green" is hypothetical at the moment: the two most recent ci runs on track/claude-auto are marked failed (cause not investigated here).
- "The CI logs show 58 skipped" was not verified from CI logs; I verified it only from the local baseline test.txt, which lists some gallery skips by name and gives the 58 total (55 gallery + 3 Desktop perf by count).
- File path and line numbers … (cut; full text in the workflow journal)

### ci-build-10. The publish and source-package scripts do not guarantee a clean, buildable, traceable output

- Final: **low** (not verified); auditor said low
- Where: `scripts/windows.sh:84`; effort S

**Evidence.** `publish` runs `dotnet publish ... -o $DIR/$APP` into an existing folder without clearing it, after `sync`, which ships `git ls-files --cached --others --exclude-standard` (the dirty working tree; the script itself prints `+dirty`). Nothing writes the SHA into the output folder. scripts/package-source.sh checks size, forbidden patterns and four required paths, but never extracts the zip and builds it; the forbidden-pattern regex also misses `.env.*` files, which .gitignore treats as secrets, and the archive includes prototypes/ and docs/reviews/, which the Windows sync deliberately excludes.

**Failure scenario.** A dependency is removed, but its DLL from an earlier publish remains in E:/mindweave-claude-auto/app, so the published app works on the test laptop and fails from a clean publish elsewhere. A source package is delivered that passes all checks but does not build from a clean extract, for example because a file needed by the build is git-ignored or untracked on the dev machine.

**Recommendation.** Delete the publish folder before publishing and write a BUILD_INFO file (SHA, dirty flag, SDK version) beside the exe. In package-source.sh, extract to a temp directory and run `dotnet build` there (or do this in CI on the archive), and add `\.env(\..*)?$` to the forbidden patterns.

