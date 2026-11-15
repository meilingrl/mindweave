# Dependencies and supply chain — grade C

The dependency set itself is good: small (8 direct product packages), centrally versioned, no known vulnerabilities, all permissive licences, at most two patch releases behind, and no prototype code leaks into the product build. What is missing is everything around it that an industrial bar expects: restores are not reproducible or integrity-checked (no lock file, no repo nuget.config, no source mapping), the distributed Windows build ships no third-party licence notices, the self-contained runtime version is whatever SDK happens to be on the publishing machine, nothing automates updates, and the whole answer-rendering path rests on a single-maintainer chain with prebuilt native code that parses untrusted model output. These are process and compliance gaps, not active defects, hence C rather than D.

**Strengths**

- Central package management is on (Directory.Build.props:14) and every version lives in Directory.Packages.props; no csproj under src/ or tests/ carries its own Version, and each package family is version-aligned (Avalonia x5 at 12.1.4, Microsoft.Extensions/Data.Sqlite at 10.0.13).
- No known vulnerabilities: the baseline vuln.txt reports no vulnerable packages for all 10 projects against nuget.org. TreatWarningsAsErrors (Directory.Build.props:7) also turns NuGet audit warnings (NU190x) into build failures, so a new advisory fails CI rather than passing silently.
- Small direct surface: Core has zero packages, Application only Logging.Abstractions, Infrastructure only Microsoft.Data.Sqlite plus DI/Logging. Credentials use source-generated P/Invoke to OS APIs instead of a third-party keychain library.
- Versions are current: outdated.txt shows Avalonia 12.1.4 vs 12.1.6, LiveMarkdown 2.5.1 vs 2.6.1, Test SDK one minor behind; the only major gaps are .NET 11 packages (correctly not adopted on a net10.0 LTS target) and xunit.v3 4.x. The bundled native SQLite (SQLitePCLRaw.lib.e_sqlite3 2.1.13) reports 3.53.3 dated 2026-06-26.
- All licences in the resolved graph are permissive (MIT, Apache-2.0, BSD-2-Clause, SIL OFL for fonts; RaTeX's native crate notices list MIT throughout). I found no copyleft or commercial-only package in src/Mindweave.Desktop/obj/project.assets.json.
- Prototypes do not leak into the product: Mindweave.slnx lists only src/ and tests/ projects, no ProjectReference points into prototypes/, and git tracks no binaries (dll/so/dylib/nupkg/fonts).
- The source adapter over LiveMarkdown is defensive: it states it uses public API only (SourceAnchors.cs:55-57), I found no reflection into library internals in src/, and stored anchors are re-validated by layout hash on restore, so a library change degrades to 'cannot locate' rather than highlighting the wrong passage.
- The LiveMarkdown.Math/RaTeX choice was a recorded decision that noted native platform coverage and licences (docs/design/decisions.md D-014), and the system-font dependency of RaTeX is documented in TRACK.md (2026-10-27).

**Not examined**

- Upstream repository health (commit cadence, open issues, contributor counts, release signing) for LiveMarkdown.Avalonia, RaTeX.Net, Onigwrap and TextMateSharp: no network lookups were made; maintainer counts are inferred from nuspec authors and decisions.md only.
- Whether the nupkg hashes in the local cache match nuget.org, and repository/author signature verification of any package.
- The actual contents of a `dotnet publish -r win-x64 --self-contained` output folder (not allowed to build/publish here); the notices and runtime-version findings are inferred from project files, the nuspecs and SDK behaviour.
- The SDK currently installed on the Windows machine (no SSH); the 10.0.103 figure comes from TRACK.md dated 2026-10-26.
- CVE status of the native libraries beyond what NuGet advisories cover (Skia, HarfBuzz, ANGLE, Oniguruma, the Rust crates inside ratex_ffi); NuGet audit does not see inside native binaries.
- The project.assets.json I read is dated 2026-10-27 and may predate later restores; the transitive versions quoted come from it and from the local package cache.
- Test-only dependency internals (xunit.v3 3.2.2, Avalonia.Headless.XUnit, Microsoft.NET.Test.Sdk) beyond version currency.
- macOS packaging, signing and notarisation of the native dylibs: no macOS bundle step exists in the repo to audit.
- The other track (track/codex-steady) and main.

### dependencies-1. Distributed builds ship no third-party licence notices, and the repo has no LICENSE or NOTICE file

- Final: **medium** (confirmed (one verifier lowered it)); auditor said high
- Where: `src/Mindweave.Desktop/Mindweave.Desktop.csproj:13`; effort S

**Evidence.** The Desktop project references LiveMarkdown.Avalonia and LiveMarkdown.Avalonia.Math (Apache-2.0), which bring Markdig (BSD-2-Clause), RaTeX/RaTeX.Avalonia (MIT plus embedded KaTeX fonts under SIL OFL 1.1), TextMateSharp/Onigwrap (MIT, bundling Oniguruma), Avalonia + Avalonia.Fonts.Inter (MIT / OFL font), SkiaSharp, HarfBuzzSharp, ANGLE natives, and via Infrastructure SQLitePCLRaw (Apache-2.0). `git ls-files` contains no LICENSE, NOTICE or THIRD-PARTY file, and the csproj has no Content/None item copying any. NuGet does not copy a package's licence files to publish output (RaTeX's sit in the package's `licenses/` folder, which is not a content folder). The published folder is handed out: docs/design/run-guide.md:39 and scripts/windows.sh:84 run `dotnet publish ... -r win-x64 --self-contained`, and TRACK.md records trial builds published to `E:\mindweave-claude-auto\app-*`. decisions.md D-014 lists the licences, so this was known but not acted on.

**Failure scenario.** The self-contained win-x64 folder (or the course submission binary) is given to anyone else. It redistributes Apache-2.0 code without a copy of the licence (section 4a), BSD-2-Clause binaries without the notice, and OFL fonts (KaTeX, Inter) without their copyright and licence text. Checkable today: publish and search the output for any licence or notice file.

**Recommendation.** Add a THIRD-PARTY-NOTICES.txt (generated from the resolved graph, including RaTeX's per-RID native notices and the KaTeX/Inter OFL texts) and include it as Content with CopyToPublishDirectory. Add a test or publish-script check that the file exists and names every package in project.assets.json. Decide and add the project's own LICENSE.

**Skeptic: partially confirmed, medium.** The factual core holds and I could not refute it. The repo tracks no LICENSE, NOTICE, COPYING or third-party notice file, and Mindweave.Desktop.csproj has no Content/None item or target that would copy one into build or publish output. No design decision, TRACK.md entry or script addresses notices. D-014 mentions the licences (Apache-2.0, MIT, SIL OFL) only as a fact about the math package; nothing follows from it. The package licences are as claimed in the local NuGet cache: LiveMarkdown.Avalonia and .Math are Apache-2.0, Markdig is BSD-2-Clause, SQLitePCLRaw.core is Apache-2.0, TextMateSharp/Onigwrap are MIT, and RaTeX ships a LICENSE file plus a `licenses/` folder with per-RID THIRD_PARTY_NOTICES and KaTeX-fonts-NOTICE.txt. That folder is not a NuGet content/runtime asset folder, so publish would not copy it. The publish commands exist as cited (run-guide.md:39, scripts/windows.sh:84).

The severity is overstated, because the claimed failure needs redistribution to a third party and nothing I found shows that has happened or is planned for this branch:
- TRACK.md line 3 says this track is explicitly not the course submission version, so the "course submission binary" part of the scenario does not apply here.
- The GitHub repo is PRIVATE with no licence set.
- The published `app-*` folders in TRACK.md went to the user's own Windows machine (E:\mindweave-claude-auto\...) for their own trial, which is not redistribution.
- There is no installer, release pipeline or download channel in the repo.
- scripts/package-source.sh ships source via `git archive`, not third-party binaries, so the source package does not trigger binary-notice obligations.

So today this is a release-readiness gap, not an active licence breach. It becomes a real compliance defect the moment a publish folder is handed to anyone else, and at the stated "industrial production quality" bar it should be fixed before any distribution. The recommendation (generated THIRD-PARTY-NOTICES copied to the publish directory, plus a check) is sound. The absence of a project LICENSE on a private repo is a separate product decision for the user, not a defect.

*Corrections to the finding.* 1. Severity high is overstated; medium fits. No redistribution to a third party has occurred or is set up: the repo is private, this track is declared not the course submission version (TRACK.md:3), and the published folders went only to the user's own Windows machine.
2. The "course submission binary" scenario does not apply to this branch; the submission track is track/codex-steady, which was not examined here.
3. "Known but not acted on" overreads D-014. It records the licences as a property of the chosen package; it does not record a recognised notice obligation that was then skipped.
4. Location line is off by a few lines: the LiveMarkdown references are at csproj lines 19-20, not 13 (line 13 opens the ItemGroup).
5. Bundling "no project LICENSE" into the same finding conflates two things. A private repo without a licence is a product decision for the user, not a compliance defect.
6. I did not independently verify the licences of the SkiaSharp, HarfBuzzSharp, ANGLE and Inter font components; the claim about them is plausible but unchecked.

**Reproduction attempt: confirmed, medium.** Not a code defect, so no xUnit test was written; verified by inspection plus an actual publish. The facts in the finding are all true at track/claude-auto 1ba95c9: (1) the tree has no LICENSE/NOTICE/THIRD-PARTY/COPYING file anywhere; (2) Mindweave.Desktop.csproj, Directory.Build.props and the scripts contain no Content/None item or step copying licence text, and src/ has no in-app licence/about text; (3) a real `dotnet publish src/Mindweave.Desktop -c Release -r win-x64 --self-contained` (the exact command in run-guide.md:39 / scripts/windows.sh:84) produced 252 files - 242 dll, 6 pdb, 2 json, 2 exe - with zero licence or notice files, while shipping LiveMarkdown.Avalonia(.Math) (Apache-2.0), SQLitePCLRaw + e_sqlite3 (Apache-2.0), Markdig (BSD-2-Clause), RaTeX/ratex_ffi (MIT + KaTeX fonts under OFL, per the package's own licenses/ folder), Onigwrap/libonigwrap (Oniguruma), SkiaSharp, HarfBuzz, ANGLE (av_libglesv2) and Avalonia.Fonts.Inter. The upstream packages do carry the texts (ratex/1.0.0/licenses/*, onigwrap THIRD-PARTY-NOTICES.TXT, skiasharp LICENSE.txt, ANGLE LICENSE) but NuGet does not flow them to publish output, confirmed empirically. decisions.md D-014 names the Apache-2.0/MIT/OFL licences, so it was known.

Severity lowered to medium: it is a real compliance gap that must be closed before any binary is handed to a third party, but there is no runtime/data/security impact, the fix is small and mechanical, and the only distribution evidenced so far is the user's own Windows test machine (E:\mindweave-claude-auto) - not a redistribution. The course deliverable built by scripts/package-source.sh is a source zip via git archive (no third-party binaries), so the "course submission binary" scenario is not what the repo's delivery script produces. The missing project LICENSE is a decision for the owner rather than a defect.

*Corrections to the finding.* 1) "The published folder is handed out" is overstated: the only evidenced destination is the user's own Windows test machine (E:\mindweave-claude-auto via scripts/windows.sh); no release, installer or third-party hand-off exists in the repo or CI. The exposure is conditional on future distribution. 2) "or the course submission binary": the repo's delivery script (scripts/package-source.sh) produces a source-only git-archive zip, which redistributes no third-party binaries; a binary submission would be affected only if a published folder is added to it. 3) I did not verify the claim that KaTeX fonts are embedded inside ratex_ffi/RaTeX assemblies beyond the package shipping KaTeX-fonts-NOTICE.txt and SIL-OFL-1.1.txt, nor that Avalonia.Fonts.Inter's nuspec (MIT) covers the embedded Inter font's OFL - plausible but not independently checked. 4) Publish output also contains 6 .pdb files (libSkiaSharp.pdb, libHarfBuzzSharp.pdb, etc.) - unrelated to licensing but worth noting for the package. Everything else in the finding checked out exactly.

<details><summary>Reproduction</summary>

No xUnit test (supply-chain/process finding). Reproduced by publishing an exported copy of track/claude-auto (git archive of src + Directory.*.props into the scratchpad, since removed):

$ dotnet publish src/Mindweave.Desktop -c Release -r win-x64 --self-contained -o out
  Mindweave.Desktop -> .../lic-repro/out/        (succeeded)

$ find out -type f | wc -l
252
$ find out -type f | grep -iE 'licen|notice|third|copying|ofl|credits|\.txt$|\.md$|\.rtf$|\.html$'
(no output)
$ find out -type f | sed 's/.*\.//' | sort | uniq -c
 242 dll
   6 pdb
   2 json
   2 exe
Third-party binaries present in out/: Avalonia.Fonts.Inter.dll, Avalonia.HarfBuzz.dll, Avalonia.Skia.dll, HarfBuzzSharp.dll, LiveMarkdown.Avalonia.dll, LiveMarkdown.Avalonia.Math.dll, Markdig.dll, Microsoft.Data.Sqlite.dll, Onigwrap.dll, RaTeX.dll, RaTeX.Avalonia.dll, SQLitePCLRaw.{batteries_v2,core,provider.e_sqlite3}.dll, SkiaSharp.dll, TextMateSharp(.Grammars).dll, av_libglesv2.dll, e_sqlite3.dll, libHarfBuzzSharp.dll, libSkiaSharp.dll, libonigwrap.dll, ratex_ffi.dll.

Cleanup: scratch export and tarball deleted; worktree /Users/meilingluo/Developer/Projects/mindweave/.claude/worktrees/wf_5c3b45e2-023-71 untouched (git status clean, nothing committed).

</details>

### dependencies-2. Restores are not reproducible or integrity-pinned: no lock file, no repo nuget.config, no package source mapping

- Final: **low** (confirmed in part); auditor said medium
- Where: `Directory.Build.props:14`; effort S

**Evidence.** Directory.Build.props enables only ManagePackageVersionsCentrally; there is no RestorePackagesWithLockFile, no packages.lock.json, no CentralPackageTransitivePinningEnabled, and no nuget.config anywhere in git. project.assets.json shows the only config in effect is the user-level `~/.nuget/NuGet/NuGet.Config`. CI runs plain `dotnet restore Mindweave.slnx` (.github/workflows/ci.yml:42), not `--locked-mode`. About 35 transitive packages are unpinned, including every native binary carrier: RaTeX 1.0.0, Onigwrap 1.0.9, SQLitePCLRaw.lib.e_sqlite3 2.1.13, SkiaSharp.NativeAssets.* 3.119.4, HarfBuzzSharp.NativeAssets.* 8.3.1.3, Avalonia.Angle.Windows.Natives.

**Failure scenario.** (1) Sources come from whatever machine restores. The Windows laptop that produces the release folder uses its own user/machine NuGet.Config; an extra feed there can serve a same-ID package and nothing in the repo detects it. (2) No content hash is recorded, so a re-published or tampered nupkg restores silently; the Mac, Windows and CI builds cannot be shown to use identical bits. (3) A transitive version changes whenever a direct package is bumped (e.g. LiveMarkdown 2.6.1 pulling a new RaTeX native) with no diff to review.

**Recommendation.** Commit a nuget.config with `<clear/>`, nuget.org only, and packageSourceMapping `*` -> nuget.org. Set RestorePackagesWithLockFile=true, commit the lock files, and restore with `--locked-mode` in CI and in scripts/windows.sh publish. Consider CentralPackageTransitivePinningEnabled for the native carriers.

**Skeptic: partially confirmed, low.** Every factual claim about the repo checks out, and I found no guard or recorded decision that refutes it. The severity is overstated: this is a missing hardening layer, not a live defect.

What holds:
- Directory.Build.props sets only ManagePackageVersionsCentrally. There is no RestorePackagesWithLockFile and no CentralPackageTransitivePinningEnabled.
- git ls-files contains no nuget.config and no packages.lock.json.
- CI restores with plain `dotnet restore Mindweave.slnx`; scripts/windows.sh publish runs `dotnet publish` with an implicit restore on the Windows machine.
- The Desktop project.assets.json lists the user-level NuGet.Config as the only config file and nuget.org as the only source.
- The Desktop graph resolves 47 packages against 15 centrally pinned ones. All the native carriers named in the finding are transitive and unpinned.
- TRACK.md and docs/design/decisions.md contain no decision accepting or addressing this (grep for lock file, locked-mode, nuget.config, packageSourceMapping found nothing relevant).

Why medium is too high:
1. Scenario 1 (extra feed serving a same-ID package) needs a second feed on the restoring machine. On this Mac the user config has only nuget.org. The Windows machine's config was not inspected (SSH is forbidden), so that part is hypothetical. With one public source there is no dependency-confusion path, and all package IDs are public nuget.org IDs.
2. Scenario 2 is partly wrong. nuget.org package versions are immutable, so "a re-published nupkg restores silently" cannot happen from nuget.org alone. It requires a different feed, a compromised feed or a poisoned local cache. The narrower true statement is that no content hash is recorded in the repo, so cross-machine bit-identity cannot be demonstrated.
3. Scenario 3 is overstated. All direct versions are exact pins in Directory.Packages.props, and NuGet resolves transitives deterministically (lowest applicable version), so they do not float between restores. They change only when someone edits a direct version, and that edit is itself a reviewable diff. What is missing is visibility of the resulting transitive changes, including native binaries.
4. The app has no release pipeline or distribution channel. "Publish" produces a local folder on the developer's own Windows machine for interactive use.

Against an industrial-quality bar the recommendation (repo nuget.config with `<clear/>` and source mapping, lock files, `--locked-mode`) is sound and cheap. It is defence in depth, so low is the right rating.

*Corrections to the finding.* - "A re-published or tampered nupkg restores silently" is overstated: nuget.org versions are immutable, so this needs a non-nuget.org feed, a feed compromise or a poisoned local cache.
- "A transitive version changes whenever a direct package is bumped with no diff to review" is overstated: the direct bump is a reviewable diff in Directory.Packages.props and resolution is deterministic; only the transitive consequences are invisible.
- "About 35 transitive packages" is roughly right for the Desktop graph: 47 resolved packages against 15 central pins, not all of which Desktop references.
- The extra-feed scenario on the Windows machine is unverified; the only config I could inspect (this Mac) has nuget.org alone.
- Severity should be low (hardening gap), not medium.

### dependencies-3. The self-contained Windows build bundles whatever .NET runtime patch the publishing machine's SDK carries

- Final: **low** (confirmed in part); auditor said medium
- Where: `scripts/windows.sh:84`; effort S

**Evidence.** Publish is `dotnet publish src/Mindweave.Desktop -c Release -r win-x64 --self-contained` (also docs/design/run-guide.md:39). global.json:3-4 pins `10.0.100` with `rollForward: latestFeature`, so any installed 10.0.x SDK is accepted. A self-contained publish bundles the runtime version baked into that SDK. TRACK.md (2026-10-26) records the Windows machine at SDK 10.0.103 while the Mac has 10.0.402 and CI uses the newest 10.0.x; TRACK.md (2026-10-27) notes CI moved from runtime 10.0.13 to 10.0.14. Nothing in the repo sets RuntimeFrameworkVersion or checks the SDK patch before publishing.

**Failure scenario.** If the laptop is still on SDK 10.0.103, the trial builds in `E:\mindweave-claude-auto\app-*` carry a runtime roughly ten servicing releases behind the one tests run on in CI. The app's HTTPS/TLS and JSON handling of remote AI responses then lacks months of runtime security fixes, and since self-contained apps are not serviced by Windows Update, the shipped folder never gets them. It also means the released binary runs on a runtime no test run used.

**Recommendation.** Publish from one controlled place (a CI job with a pinned SDK, uploading the artifact), or have the publish script fail when `dotnet --version` is below a minimum patch. Record the bundled runtime version next to each published build.

**Skeptic: partially confirmed, low.** The mechanism is real and I found no guard against it, but the impact is overstated for what this publish path is actually used for.

**What holds:**
- `scripts/windows.sh:84` runs `dotnet publish src/Mindweave.Desktop -c Release -r win-x64 --self-contained` on the remote Windows machine, with no SDK version check before it. The `test` verb prints `dotnet --version`; `publish` does not.
- `global.json` pins `10.0.100` with `rollForward: latestFeature`, so any installed 10.0.x SDK is accepted.
- No `RuntimeFrameworkVersion`, `TargetLatestRuntimePatch` or similar appears in any csproj/props/targets, so a self-contained publish bundles the latest runtime patch that SDK knows about.
- `TRACK.md:126` records the Windows machine at SDK 10.0.103 and the Mac at 10.0.402; this was a deliberate choice to let both machines build. `TRACK.md:130` records CI moving from runtime 10.0.13 to 10.0.14.
- CI uses `dotnet-version: 10.0.x` and has no publish or upload-artifact job, so there is no controlled publish path.
- Nothing in `docs/design/decisions.md` or `TRACK.md` accepts or addresses the runtime-patch drift for published builds.

**What is overstated:**
- The script header describes `publish` as producing an exe "for interactive use" on the "Windows 11 test machine". The `app-*` folders in `TRACK.md:40-46` are the owner's own trial builds on their own machine, not a distributed release. No installer, release pipeline or end-user distribution exists on this track, and `AGENTS.md` says the course submission is the other track.
- "The shipped folder never gets fixes" therefore describes a hypothetical future release. Each trial build is republished per stage.
- "The released binary runs on a runtime no test run used" is only partly true. `scripts/windows.sh test` runs the full suite on the same Windows machine with the same SDK, so that runtime is exercised whenever the script is used. `TRACK.md:40` notes Windows real-machine testing has been paused since 2026-11-05.
- The security exposure is narrow: the app is an HTTPS client to AI endpoints the user configures. A stale runtime is a hygiene gap, not a directly exploitable path.

**Not verified:** whether the Windows machine is still on SDK 10.0.103. The last record is 2026-10-26 and SSH was out of bounds for this audit. The finding itself hedges with "if".

**Severity:** low today, as a reproducibility and hygiene gap in trial builds. It becomes medium the moment a build from this script is handed to anyone else, so it should be fixed before any real release. The recommendation (minimum SDK patch check in the publish verb, record the bundled runtime version, or publish from CI) is sound and cheap.

*Corrections to the finding.* - The builds affected are the owner's own interactive trial builds on their test machine, not a shipped or released binary. There is no distribution channel on this track, so "shipped folder never gets fixes" describes a future risk.
- "Runs on a runtime no test run used" is too strong: `scripts/windows.sh test` runs the suite on the same machine and SDK. Only CI and the Mac use a different patch.
- Whether the Windows machine is still on SDK 10.0.103 is unverified; the only record is dated 2026-10-26.
- If it is, the bundled runtime would be about 10.0.3 against CI's 10.0.14, so "roughly ten servicing releases behind" is about right.
- Severity should be low now, rising to medium as a pre-release gate.

### dependencies-4. Answer rendering rests on a single-maintainer chain that runs prebuilt native code on untrusted model output

- Final: **low** (confirmed in part); auditor said medium
- Where: `Directory.Packages.props:9`; effort M

**Evidence.** LiveMarkdown.Avalonia and LiveMarkdown.Avalonia.Math 2.5.1 have one author (nuspec `<authors>DearVa</authors>`; decisions.md D-014 calls the math package '同一作者的配套包'). They pull RaTeX 1.0.0 and RaTeX.Avalonia 1.0.0 (author 'Sylinko'), whose README describes itself as 'Unofficial .NET bindings' over a third party's Rust engine, first stable release in 2026, shipping ~3 MB prebuilt `ratex_ffi` binaries per RID. They also pull TextMateSharp 2.0.0 -> Onigwrap 1.0.9 (single author), shipping a native Oniguruma regex engine. All text these parse is LLM output. MathRenderingTests.cs has three tests, all with well-formed formulas; I found no test feeding malformed or hostile LaTeX or pathological code blocks. The only crash handling is logging in Program.cs:36-37.

**Failure scenario.** A model emits a formula or code fence that triggers a fault in ratex_ffi or libonigwrap (memory error, Rust abort, or catastrophic regex backtracking on the UI thread). A native fault cannot be caught by managed code: the process dies or hangs mid-stream, and because the answer text is stored, reopening that conversation can re-trigger it every time. Separately, if the maintainer stops (e.g. no Avalonia 13 build), the app cannot move Avalonia forward without replacing the renderer that SourceAnchors, SelectionCarry and AnswerView are written against.

**Recommendation.** Add a fuzz/corpus test that renders malformed LaTeX and oversized code blocks through the real pipeline on all three OSes. Add a safe-mode path (render a message as plain text after a crash while rendering it). Write down the exit plan in TRACK.md: the fork-or-replace option and which LiveMarkdown types the adapter depends on.

**Skeptic: partially confirmed, low.** The factual inventory is accurate, but the failure scenario is hypothetical and the "single-maintainer chain" framing is overstated, so this is a low-severity risk item rather than a medium defect.

What holds up:
- The dependency chain is as described. LiveMarkdown.Avalonia and LiveMarkdown.Avalonia.Math 2.5.1 are both by DearVa. They pull RaTeX and RaTeX.Avalonia 1.0.0 (Sylinko), whose README calls itself an "unofficial" binding over a third party's engine, and TextMateSharp 2.0.0, which pulls Onigwrap 1.0.9.
- Prebuilt native binaries ship for each RID: `ratex_ffi` at about 2.8-3.6 MB and `libonigwrap` at about 0.5 MB. Both are in the app's deps.json.
- No test feeds malformed LaTeX or pathological code blocks. MathRenderingTests has three tests on one well-formed answer; a grep of the test tree for malformed/unbalanced/unclosed/fuzz found nothing relevant.
- The only crash handling is logging (Program.cs:36-37 and LogCrash). The try/catch blocks in AnswerView and SourceAnchors cover anchor restore only, and I found no plain-text fallback.
- No exit plan for the renderer is written down in TRACK.md or decisions.md. Five source files plus two axaml files are coupled to LiveMarkdown types.
- Neither D-014 nor TRACK.md records this risk as accepted.

What is overstated:
- No crash, hang or memory fault was demonstrated; the scenario is conjecture. I could not run a reproduction under the read-only rules.
- RaTeX's managed binding exposes `ratex_get_last_error`, `RatexError` and `ErrorCode`, so ordinary malformed LaTeX has an error-return path. TRACK.md:141 also records a formula that could not be drawn falling back to LaTeX source rather than crashing.
- The engine is Rust, so memory-corruption faults are unlikely. The realistic residual is a panic that aborts the process: the dylib contains "panic in a function that cannot unwind" strings, and I could not tell whether the FFI wraps calls in catch_unwind.
- The TextMateSharp/Onigwrap leg is not a one-person chain. It has two further distinct authors, TextMateSharp is the stack AvaloniaEdit uses, and Oniguruma is a mature engine. Catastrophic backtracking would need a bad grammar regex, not just hostile input, and nothing shows highlighting runs on the UI thread.
- "Model emits hostile output" is a reliability concern, not a security boundary: this is a single-user desktop app talking to the user's own configured provider.

The recommendations (a malformed-input corpus test, a plain-text fallback, a written exit plan) are reasonable hardening for the stated quality bar.

*Corrections to the finding.* - "Single-maintainer chain" is true only of the LiveMarkdown pair (DearVa) and the RaTeX pair (Sylinko). TextMateSharp (Daniel Peñalba) and Onigwrap (Aikawa Yataro) are separate authors, and that stack is widely used through AvaloniaEdit.
- "A native fault ... memory error" is unlikely for the Rust engine; the realistic mode is a panic abort. The binding has an error-return API, so malformed LaTeX is not automatically a fault. No fault was demonstrated.
- Catastrophic regex backtracking on the UI thread is unsupported: it would need a pathological grammar regex, and LiveMarkdown parses in the background per decisions.md:35; where highlighting runs was not shown.
- "Untrusted model output" should read as a robustness concern, not a security one.
- "First stable release in 2026" was not verified beyond the 1.0.0 version and an Oct 19 file date.
- Severity should be low: a hardening and documentation gap (no malformed-input test, no plain-text fallback, no written exit plan), not a known defect.

### dependencies-5. No automated dependency updates, and GitHub Actions are pinned by mutable tag

- Final: **low** (not verified); auditor said low
- Where: `.github/workflows/ci.yml:31`; effort S

**Evidence.** .github/ contains only ci.yml and stress.yml: no dependabot.yml or Renovate config. Actions are referenced as `actions/checkout@v4` and `actions/setup-dotnet@v4` (ci.yml:31-32, stress.yml:36-37), tags rather than commit SHAs. outdated.txt already shows drift: Avalonia 12.1.4 -> 12.1.6, LiveMarkdown 2.5.1 -> 2.6.1, Microsoft.NET.Test.Sdk 18.11.0 -> 18.12.0, xunit.v3 3.2.2 -> 4.0.3. ci.yml:24 runs macOS only on manual dispatch, so the osx native assets (RaTeX, Skia, e_sqlite3) are never exercised in CI on the platform requirements.md:118 names as primary.

**Failure scenario.** A security fix lands in Avalonia, Skia or SQLite natives; nobody is told until someone runs `dotnet list package --outdated` by hand. A retagged or compromised action runs in CI unnoticed. A dependency bump that breaks only on macOS passes CI.

**Recommendation.** Add Dependabot (nuget + github-actions ecosystems) targeting track/claude-auto, pin actions to commit SHAs, and run the macOS job at least on dependency-changing commits (changes to Directory.Packages.props).

### dependencies-6. The persisted render-profile name hard-codes the LiveMarkdown version and nothing ties it to the package actually resolved

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Desktop/Markdown/SourceAnchors.cs:64`; effort S

**Evidence.** `public const string RenderProfile = "livemarkdown-2.5.1/math-3";` is written into every branch source row (AnswerView.axaml.cs:112, SqliteBranchStore.cs:47), and RevealSource refuses any profile not in the hand-maintained CompatibleProfiles set (AnswerView.axaml.cs:124; SourceAnchors.cs:73-79). The version lives separately in Directory.Packages.props:9-10. No test compares the constant with the loaded LiveMarkdown assembly version; the only profile assertion is MarkdownRenderingTests.cs:211, which checks that an old name is still in the set.

**Failure scenario.** Upgrade to 2.6.1 (already available). Either the constant is left alone and the stored name misdescribes the renderer from then on, or it is renamed and the old name is not added to CompatibleProfiles, in which case every existing branch's 'show source' returns null and silently stops highlighting. The layout-hash check prevents a wrong highlight, so impact is limited to lost source reveal.

**Recommendation.** Add a test asserting the profile's version segment equals the LiveMarkdown.Avalonia assembly version, and one asserting every profile name ever shipped is still in CompatibleProfiles, so a package bump forces a deliberate decision.

### dependencies-7. Prototypes cited as verification evidence can no longer be built in this tree

- Final: **low** (not verified); auditor said low
- Where: `prototypes/livemarkdown-chat/src/LiveMarkdownSpike/LiveMarkdownSpike.csproj:4`; effort S

**Evidence.** The root Directory.Build.props applies to everything beneath it, including prototypes/, and sets ManagePackageVersionsCentrally=true and TreatWarningsAsErrors=true. All three prototypes declare inline versions (`<PackageReference Include="Avalonia.Desktop" Version="12.1.4" />`; avalonia-chat uses Avalonia 11.3.21, xunit 2.9.3, net8.0) and git tracks no Directory.Build.props or Directory.Packages.props under prototypes/ to stop inheritance. Inline versions under central management are a restore error (NU1008). Not verified by building, since building was out of bounds. docs/architecture.md:179 and :447 cite the LiveMarkdown prototype as evidence ('8/8').

**Failure scenario.** Someone on the track branch tries to re-run the LiveMarkdown spike to re-validate anchor behaviour before a library upgrade; `dotnet test` in prototypes/livemarkdown-chat fails at restore. The good side is that prototypes cannot leak into the product build: Mindweave.slnx lists none of them.

**Recommendation.** Add a prototypes/Directory.Build.props that does not import the root one (and sets ManagePackageVersionsCentrally=false), or state in each prototype README that it builds only on main.

### dependencies-8. RaTeX ships natives for win-x64 only on Windows; an Arm64 or x86 Windows build would have no formula engine

- Final: **info** (not verified); auditor said info; already known in TRACK.md
- Where: `Directory.Packages.props:10`; effort S

**Evidence.** RaTeX 1.0.0 `runtimes/` contains win-x64, osx-arm64, osx-x64, linux-x64 and linux-arm64 only, whereas SQLite, Skia, HarfBuzz and Onigwrap also ship win-arm64. requirements.md:118 scopes Windows to 'Windows 11 x64' and decisions.md D-014 records this coverage, so it is within the stated scope today.

**Failure scenario.** A later `-r win-arm64` publish builds and starts, then fails to load ratex_ffi the first time an answer contains a formula. Nothing in the build would warn.

**Recommendation.** No action for v1. If the platform list grows, add a publish-time check that every native-carrying package has an asset for the target RID.

