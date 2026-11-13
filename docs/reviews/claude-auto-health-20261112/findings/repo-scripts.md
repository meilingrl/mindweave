# Repository hygiene and scripts — grade B

The scripts are well above typical project quality: all five bash scripts use set -euo pipefail, quote consistently, retry only connection-level SSH failures, validate message ids before interpolating them into remote PowerShell, and keep API keys in the keychain. A scan of the full history (427 commits, all branches, 14.6 MB of diffs) found no key-like strings, no private keys and no tracked binaries on this track; the repository is 8.8 MB. What keeps it from an A is the state around the code rather than in it: 112 commits of the course-submission track exist only on this disk, review evidence lives only as untracked files in a stale worktree, 14 dead branches and 2 stale worktrees contradict the repo's own clean-up rule, the tracked prototypes no longer restore on this branch, the delivery-package check has gaps and litters the worktree, and CI has no token permissions, pinned actions or lock file. None of these is a product defect; several are one-line fixes.

**Strengths**

- No secrets in history: grepping `git log --all -p` for sk-, AKIA, ghp_, github_pat_, xox*, PRIVATE KEY, Bearer tokens, tskey- and AIza returned only false positives (`task-...` log names). scripts/real-api.sh:36 has `security` prompt for the key itself, so it never reaches argv, the repo or shell history; the profile file is written with `%q` under umask 077 (lines 37-38).
- Exit codes are deliberately preserved across the SSH/PowerShell boundary: scripts/windows.sh:80 and :84 end with `exit $LASTEXITCODE` after the filter pipeline, and retry_ssh (lines 28-38) retries only exit 255, never a command that ran and failed. TRACK.md (2026-10-28) records the earlier `| tail` exit-code-swallowing incident that led to this.
- Remote command injection is closed where it matters: scripts/mailbox.sh:55 validates ids against `^[0-9]{8}-[0-9]{6}-[mw]-[a-z0-9-]+$` before they are placed in PowerShell strings, kind is whitelisted (line 73), the slug is sanitised (line 75), and scripts are sent with -EncodedCommand so quoting survives cmd.exe.
- Message writes are atomic on both sides (dot-named temp file then rename: mailbox.sh:50-53, mailbox.ps1:64-66), and windows.sh:66-70 verifies the remote file count after a sync because a relay drop can leave a partial tree with exit 0.
- .gitattributes forces `* text=auto eol=lf` with a comment explaining the real defect behind it (CRLF checkout changing raw string literals), matching the TRACK.md finding of 2026-10-27.
- Commit hygiene is good: 309 commits on the track, median subject length 70 characters, no wip/fix/tmp-style subjects, descriptive messages, a single author identity, and one baseline tag. No blob over 320 KB exists anywhere in history; no binaries are tracked on this branch.
- scripts/package-source.sh uses `git archive`, so untracked and ignored files cannot enter the delivery package by construction, and it still checks size, forbidden patterns and required files, exiting non-zero on failure.

**Not examined**

- Did not run any script: no SSH to the Windows machine, no keychain access, no real API calls, so remote PowerShell behaviour (exit codes of -EncodedCommand, scp to E:/ paths, tar.exe semantics) is judged from reading only.
- Did not inspect the GitHub repository settings (visibility, default GITHUB_TOKEN permissions, branch protection, Actions run history); the CI finding is from the workflow files alone.
- Did not read the contents of other worktrees beyond `git status`, nor the track/codex-steady scripts or its tracked .db fixtures (311 KB each, present only in that branch's history).
- History secret scan was pattern-based on added lines; no entropy-based scanner (gitleaks/trufflehog) was run, and the mailbox folder and reports on the Windows machine were not examined.
- prototypes/responses-compat Python probes and the two larger prototypes were only skimmed for secrets and build setup, not reviewed as code.
- Baseline outputs (build/test/vuln/outdated/format) in the scratchpad were not consulted for this dimension.

### repo-scripts-1. 112 commits of the course-submission track exist only on this machine

- Final: **high** (confirmed (one verifier lowered it)); auditor said high
- Where: `AGENTS.md:10`; effort S

**Evidence.** `git branch -vv` shows `track/codex-steady 07baf94 [origin/track/codex-steady: ahead 112]`. The unpushed commits are dated 2026-10-26 to 2026-10-28 (38 + 45 + 29); the remote tip is from 2026-10-25. AGENTS.md line 10 names this branch as the course submission version. `main` is also ahead of origin by 3 (requirements v0.12 and v0.13), though that content is reachable from the pushed track/claude-auto. The worktree branch worktree-wf_e0cb2e5e-84d-1 holds 2 further local-only commits (items 7a/7b, migration v11) from 2026-11-12.

**Failure scenario.** The Mac's disk fails, or a mistaken `git branch -D` / worktree clean-up happens: over two weeks of the graded track's work (112 commits) is unrecoverable, because the only other copy on origin stops at 2026-10-25. All worktrees share one .git directory, so there is no second local copy either.

**Recommendation.** Push track/codex-steady and main to origin (TRACK.md records push authorization only for track/claude-auto, so this needs the user's decision), or add a second remote or bundle backup. Consider a periodic check that reports any branch ahead of its upstream.

**Skeptic: partially confirmed, high.** The core claim holds and I could not refute it: the course-submission branch has 112 commits that exist only in this machine's single shared .git directory.

What I checked:
- `track/codex-steady` (07baf94) is `[origin/track/codex-steady: ahead 112]`. The unpushed commits are dated 2026-10-26 (38), 10-27 (45) and 10-28 (29). The remote-tracking ref was last moved by a push on 2026-10-25 22:50.
- The unpushed delta is large: 292 files changed, 33,584 insertions against the remote tip.
- AGENTS.md line 10 does name this branch as the course submission version.
- There is one remote only (GitHub origin), and every worktree's gitdir points into `/Users/meilingluo/Developer/Projects/mindweave/.git`, so there is no second local object store.
- `tmutil destinationinfo` reports no Time Machine destination, so I found no machine-level backup either.
- I found no decision that makes this intended. TRACK.md line 48 authorizes pushing only `origin/track/claude-auto`. The codex-steady TRACK.md has no push or backup statement.
- The Windows clones described in TRACK.md line 94 are tester clones by SHA; I found no sign they hold codex-steady, and I did not SSH to check.

Why only partially confirmed:
- "Over two weeks of work" is wrong. It is three days of commits (Oct 26-28); the branch has been idle and unpushed for about 16 days since.
- A mistaken `git branch -D` would not make the commits unrecoverable; they stay reachable through the reflog until gc. Disk loss, deleting the repository directory, or theft are the real scenarios.
- `main` being ahead by 3 is a recorded user decision, … (cut; full text in the workflow journal)

*Corrections to the finding.* 1. "Over two weeks of the graded track's work" is overstated: the 112 commits span three days (2026-10-26 to 10-28). They have been unpushed for about 16 days.
2. A mistaken `git branch -D` is recoverable through the reflog until gc. The unrecoverable scenarios are disk failure, loss of the machine, or deleting /Users/meilingluo/Developer/Projects/mindweave/.git.
3. `main` ahead by 3 is a recorded user decision (TRACK.md line 31), and its content is already on `origin/track/claude-auto`. It should not count as part of the defect.
4. The two worktree-branch commits are day-old in-progress work and should be dropped from the finding or listed as informational.
5. Worth adding to the finding: no Time Machine destination is configured, which strengthens the "no second copy" claim.
6. The remote state was inferred from local tracking refs, not checked against GitHub.

**Reproduction attempt: confirmed, high.** This is a process/backup finding, not a code defect, so no xUnit test applies; I verified it with read-only git commands from my own worktree, including a live `git ls-remote --heads origin` so the result does not depend on possibly stale remote-tracking refs.

Confirmed facts:
- origin (the only remote) has exactly three heads: main dbc5705, track/claude-auto 1ba95c9, track/codex-steady 6645088. The live remote tip of track/codex-steady matches the local remote-tracking ref (dated 2026-10-25).
- Local track/codex-steady is at 07baf94, 112 commits ahead. `git rev-list --count track/codex-steady --not --remotes` = 112, so none of those commits are reachable from any remote ref. Dates: 38 on 2026-10-26, 45 on 2026-10-27, 29 on 2026-10-28 — exactly as claimed. The unpushed delta is 292 files changed, 33,584 insertions.
- AGENTS.md line 10 does name track/codex-steady as the course submission version.
- main is 3 ahead of origin/main (bf4b549 v0.12, 1021b69 D7, 1086f87 v0.13), but `git rev-list --count main --not --remotes` = 0, so that content is safe via the pushed track/claude-auto — as the finding says.
- worktree-wf_e0cb2e5e-84d-1 (3558693) has 2 commits on no remote ref — as claimed.
- All worktrees (git worktree list shows 9) share the single repository at /Users/meilingluo/Developer/Projects/mindweave, so there is no second local object store.

The failure scenario is real for disk loss or loss of the machine: the graded track's implementation exists in one place. Severity high is appropriate given that this is the course-submission branch and the fix is a single push ( … (cut; full text in the workflow journal)

*Corrections to the finding.* 1. "Over two weeks of the graded track's work" is overstated: the 112 commits span three days (2026-10-26 to 2026-10-28). What is true is that they have sat unpushed for about 16 days, and that the branch has had no commits since 2026-10-28. The volume is still large (292 files, ~33.6k inserted lines).
2. The claim that a mistaken `git branch -D` or worktree clean-up makes the work "unrecoverable" is too strong: after a branch deletion the commits remain in the object store and are recoverable from the reflog until gc expiry (default 90 days for reachable entries), and removing a worktree does not delete its branch. Only disk/machine loss or deletion of the shared .git directory is truly unrecoverable.
3. "No second copy" is verified only at the git level (one remote, one shared object store). I did not check for Time Machine or other out-of-band backups.
4. Minor: the local branches tra … (cut; full text in the workflow journal)

<details><summary>Reproduction</summary>

Not reproducible by a test (process/backup finding). Verified by direct inspection with read-only git commands; key output:

$ git ls-remote --heads origin
dbc57053bea7541cec7d357bc3b7c1b709ad0006 refs/heads/main
1ba95c9e76d4537727231b3af4c980df2efdcd66 refs/heads/track/claude-auto
6645088a9b7426810b19aa389ac1d0598de41f28 refs/heads/track/codex-steady

$ git rev-list --count track/codex-steady --not --remotes
112
$ git rev-list --count main --not --remotes
0
$ git rev-list --count worktree-wf_e0cb2e5e-84d-1 --not --remotes
2
$ git diff --shortstat origin/track/codex-steady track/codex-steady
 292 files changed, 33584 insertions(+), 3 deletions(-)

</details>

### repo-scripts-2. Codex review evidence exists only as untracked files in a stale worktree, linked by absolute path

- Final: **low** (confirmed in part); auditor said medium
- Where: `docs/reviews/claude-auto-architecture-20261101/README.md:29`; effort S

**Evidence.** Line 29 links to `/Users/<user>/Developer/Projects/mindweave.worktrees/codex-arch-review/docs/reviews/claude-auto-architecture-20261101/codex-review.md`; proposal.md:6 and discussion.md:134 and :176 do the same. In that worktree `git status --short --untracked-files=all` shows `?? .../codex-review.md` and `?? .../codex-review-round2.md`; `git ls-files` there returns nothing for the directory. The branch codex/claude-auto-arch-review (6843143) is fully merged and 211 commits behind track/claude-auto.

**Failure scenario.** Someone follows AGENTS.md rule 6 (remove merged task worktrees) and runs `git worktree remove --force` on codex-arch-review: both Codex review documents, which the tracked proposal says it was revised in response to, are deleted with no copy in any commit. Independently, the link is dead for every reader other than this Mac, including the Windows clone and the source package.

**Recommendation.** Commit the two Codex review files into docs/reviews/claude-auto-architecture-20261101/ on the track (user decision, since they came from the other agent), and change the four absolute links to relative ones. Then the stale worktree can be removed safely.

**Skeptic: partially confirmed, low.** I could not refute the facts; every cited one checks out. The severity is overstated, so low rather than medium.

**What holds**
- The four absolute links exist as cited (README.md:29, proposal.md:6, discussion.md:134 and :176).
- Both Codex review files exist only as untracked files in the codex-arch-review worktree. A filesystem search of the main checkout and all worktrees found no other copy, and no ref has ever contained a file of that name.
- The tracked review directory on track/claude-auto holds only README.md, discussion.md, proposal-round1.md and proposal.md.
- Branch codex/claude-auto-arch-review sits at 6843143, which is an ancestor of HEAD and 211 commits behind it.
- The links are dead on any other machine or clone.

**Why it is low, not medium**
- **The state is deliberate and disclosed.** README.md:29 itself says the review is "kept in Codex's own worktree (branch `codex/claude-auto-arch-review`, uncommitted)", and discussion.md:134 says the same. This is a recorded choice, consistent with AGENTS.md rule 5 (do not touch another agent's changes), not an unnoticed accident.
- **The loss scenario needs a deliberate override.** Plain `git worktree remove` refuses when the worktree has untracked or modified files; only `--force` deletes them. AGENTS.md rule 5 also forbids deleting another session's uncommitted changes without the user confirming.
- **Rule 6 does not straightforwardly apply.** The branch has no commits of its own, so "merged" is only trivially true; it is not a completed, merged task branch.
- **The substance survives in tracked files.** proposal. … (cut; full text in the workflow journal)

*Corrections to the finding.* - **"Fully merged" is misleading.** The branch codex/claude-auto-arch-review has zero commits of its own; it simply points at 6843143, an ancestor of the track. It is an idle branch, not a merged task.
- **The failure scenario omits two guards.** `git worktree remove` without `--force` refuses on untracked files, and AGENTS.md rule 5 requires user confirmation before deleting another session's changes.
- **The untracked state is explicitly documented** in README.md:29 and discussion.md:134 ("uncommitted"), which the finding does not mention.
- **The untracked set is larger than stated.** The Codex worktree also has an untracked README.md in that directory and a modified docs/reviews/README.md.
- **A sibling worktree is in a similar state.** claude-auto-arch-review (also at 6843143) has the whole review directory untracked plus a modified docs/reviews/README.md; I did not compare its copi … (cut; full text in the workflow journal)

### repo-scripts-3. Tracked prototypes no longer restore on this branch: root Central Package Management applies to them

- Final: **low** (confirmed in part); auditor said medium
- Where: `Directory.Build.props:14`; effort S

**Evidence.** Directory.Build.props:14 sets `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>` at the repo root. No prototype has its own Directory.Build.props, so MSBuild imports the root one. prototypes/network-layout/Network.csproj and prototypes/avalonia-chat/src/Mindweave.Desktop/Mindweave.Desktop.csproj use `<PackageReference ... Version="..."/>`. Reproduced in a scratch copy (root props plus prototypes/network-layout): `dotnet restore` fails with `error NU1008: The following PackageReference items cannot define a value for Version: Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter`. The root props would also force TreatWarningsAsErrors and AnalysisMode=Recommended on prototype code.

**Failure scenario.** A reader follows prototypes/network-layout/README.md:9 (`dotnet run -c Release`) or prototypes/avalonia-chat/README.md:73-74 on track/claude-auto or from the source zip: restore fails immediately. The network prototype is the approved visual baseline for S7 (docs/design/s7-prototype-visual-language.md:3 says its values come from that code on this branch), so the reference can no longer be run for comparison. An orphan directory `mindweave.worktrees/claude-auto-token-calib/prototypes` outside any worktree suggests this was already worked around by copying.

**Recommendation.** Add a `prototypes/Directory.Build.props` (and empty `Directory.Packages.props`) that stops the upward import and sets ManagePackageVersionsCentrally=false, or exclude prototypes via a condition in the root props. Add a CI step that restores at least the network prototype if it stays the visual reference.

**Reproduction attempt: partially confirmed, low.** The mechanism is real and I reproduced it, but the impact is overstated.

What holds:
- Root Directory.Build.props line 14 sets ManagePackageVersionsCentrally=true.
- No Directory.Build.props, Directory.Packages.props or opt-out property exists anywhere under prototypes/ (the only other props files tracked are under tests/).
- All three prototypes use PackageReference with an inline Version, so all of them are affected, not just the two named.
- Restore of the network prototype fails with NU1008 in a scratch copy.
- The same failure is already recorded in the real worktree: prototypes/network-layout/obj/project.assets.json (written Nov 12) has CPM enabled and an NU1008 entry in its logs.
- No CI step or script builds the prototypes, so nothing catches this. scripts/windows.sh:59 explicitly excludes prototypes from the Windows sync.
- Nothing in TRACK.md or docs/design/decisions.md records this as known or intended.

Why medium is too high:
- The prototypes are declared standalone and discardable (network-layout README line 3), and no product code, test or script depends on them.
- The network README's run instructions point at the main checkout (`cd .../Projects/mindweave/prototypes/network-layout`). main has no Directory.Build.props, so the approved visual reference still runs there. "The reference can no longer be run for comparison" is only true from this branch's tree.
- docs/design/s7-prototype-visual-language.md:3 already says its screenshots were rendered "from a scratch copy", so the existing workflow sidesteps the problem, and S7 is marked complete in TRACK.md:33. … (cut; full text in the workflow journal)

*Corrections to the finding.* 1. Severity should be low, not medium: this is a hygiene and documentation-accuracy defect in discardable prototypes with no product, test or CI impact and a trivial workaround.
2. "The reference can no longer be run for comparison" is overstated. The README's run instructions point at the main checkout, where no root Directory.Build.props exists and the prototype is unaffected. Only running from the track/claude-auto tree (or a source zip of that branch) fails.
3. The orphan directory claude-auto-token-calib/prototypes is weak evidence of a workaround: it contains only avalonia-chat/tests, not the network prototype, and looks more like leftover build output from a removed worktree.
4. The finding under-reports scope: prototypes/livemarkdown-chat and the avalonia-chat test project also use inline versions and are affected the same way.
5. The TreatWarningsAsErrors / AnalysisMode conseque … (cut; full text in the workflow journal)

<details><summary>Reproduction</summary>

Scratch copy outside any worktree, at /private/tmp/claude-501/-Users-meilingluo-Developer-Projects-mindweave-worktrees-claude-auto/741bda69-6233-404f-b4a0-8bc89d0ed5bc/scratchpad/verify-nu1008: root Directory.Build.props and Directory.Packages.props, plus prototypes/network-layout/{Network.csproj,Program.cs,LocalMotion.cs,global.json}.

Ran `dotnet restore` in prototypes/network-layout. Output:

  Determining projects to restore...
  .../verify-nu1008/prototypes/network-layout/Network.csproj : error NU1008: The following PackageReference items cannot define a value for Version: Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter. Projects using Central Package Management must define a Version value on a PackageVersion item.
  Failed to restore .../Network.csproj (in 19 ms).

Nothing was built or written in the audited worktree; `git status --short` there was still clean afterwards.

</details>

### repo-scripts-4. Stale branches and worktrees accumulate against the repo's own clean-up rule; main checkout always shows as dirty

- Final: **low** (confirmed in part); auditor said medium
- Where: `.gitignore:1`; effort S

**Evidence.** `git branch -a` lists 14 `worktree-wf_*` branches all at dbc5705 with no worktree attached, plus worktrees claude-auto-arch-review and codex-arch-review at 6843143 (both merged, 211 behind, each with a modified docs/reviews/README.md and untracked files). `git count-objects -v` reports `garbage found: .git/worktrees/claude-auto/refs`. The workflow harness places worktrees under `mindweave/.claude/worktrees/`, and .gitignore (14 lines) has no `.claude/` entry, so `git status` in the main checkout shows `?? .claude/`. AGENTS.md rule 6 says task worktrees and branches are deleted after merge; rule 1 says any uncommitted change not your own means the directory is occupied.

**Failure scenario.** An agent starting work on main runs the mandated `git status`, sees `?? .claude/`, and either refuses to write there (false 'occupied') or learns to ignore dirty status, which defeats the check that prevented the 2026-10-26 overwrite incident recorded in TRACK.md. Separately, the 14 dead branch names make it hard to tell which worktree branch (for example wf_e0cb2e5e-84d-1, which holds unmerged items 7a/7b) is live.

**Recommendation.** Add `.claude/` to .gitignore on main; delete the 14 merged `worktree-wf_*` branches (`git branch -d` succeeds since they are ancestors); remove the two arch-review worktrees after rescuing their untracked files; remove the orphan claude-auto-token-calib directory. These touch other sessions' state, so they need the user's go-ahead.

**Skeptic: partially confirmed, low.** The factual observations hold, but the claimed impact is speculative and the severity is overstated; this is local-machine clutter, not a defect in repository content.

**What I confirmed**
- **Stale branches:** 17 local `worktree-wf_*` branches sit at dbc5705, which is an ancestor of main. Three belong to the currently running workflow (wf_5c3b45e2-023-66/76/88; two are locked by a live agent pid), leaving exactly 14 leftovers with no worktree. None exist on origin.
- **Arch-review worktrees:** both are at 6843143, an ancestor of track/claude-auto and 211 commits behind it. Each has a modified `docs/reviews/README.md` and an untracked `docs/reviews/claude-auto-architecture-20261101/`.
- **Dirty main checkout:** `.gitignore` is 14 lines with no `.claude/` entry, identical on main, and `.git/info/exclude` has no entry either. `git status --short` in the main checkout prints `?? .claude/`.
- **Garbage warning:** `git count-objects -v` reports `.git/worktrees/claude-auto/refs` as garbage.
- **Unmerged work:** `worktree-wf_e0cb2e5e-84d-1` is 2 commits ahead of track/claude-auto (items 7a and 7b).
- **Incident and rules:** TRACK.md line 123 records the 2026-10-26 overwrite incident; AGENTS.md rules 1 and 6 read as quoted.

**Why the severity is overstated**
- **The failure scenario is hypothetical.** No evidence shows an agent refusing to write or ignoring status because of `?? .claude/`. It is a single untracked directory with an obvious harness origin, and main carries no product code. Rule 1 also asks agents to check `git worktree list` and other sessions, so the check does n … (cut; full text in the workflow journal)

*Corrections to the finding.* - **Branch count:** there are 17 `worktree-wf_*` branches at dbc5705, not 14. The figure of 14 is right only for leftovers from earlier runs; the other three belong to the live workflow (wf_5c3b45e2), and two of those have locked worktrees.
- **Uncommitted work in the 7a/7b worktree:** `wf_e0cb2e5e-84d-1` has at least 5 modified files on top of its 2 unmerged commits. The finding does not mention this, and any clean-up must not touch that worktree.
- **Rule 6 framing:** the 14 branches are harness-created placeholders that never diverged from dbc5705, not merged task branches. This is harness residue rather than a clear breach of rule 6.
- **Incident link:** the 2026-10-26 incident was two concurrent writers in one directory, so "defeats the check that prevented the incident" is a stretch.
- **Minor items:** the "garbage" is a single empty directory, and the token-calib directory holds o … (cut; full text in the workflow journal)

### repo-scripts-5. package-source.sh drops its zip into the worktree, leaves it on failure, and its content check has gaps

- Final: **low** (confirmed in part); auditor said medium
- Where: `scripts/package-source.sh:13`; effort S

**Evidence.** Line 13: `OUT="${2:-$PWD/mindweave-source-$SHA.zip}"`; docs/design/run-guide.md:47 tells the reader to run `scripts/package-source.sh` from the repo root, and .gitignore has no `*.zip` entry. Line 16 writes the archive before any check, and line 50 exits non-zero without removing it. Line 28's deny pattern ends with `\.env$`, so `.env.local` or `prod.env.bak` pass, and it has no entry for `.pem`, `.zip` or logs. Line 31 `printf '  %s\n' $bad` is unquoted, so names with spaces are split and glob characters expand. No `export-ignore` attributes exist, so `.keep`, AGENTS.md, CLAUDE.md, TRACK.md, .github/ and docs/reviews (which contain absolute home-directory paths) all go into the course package.

**Failure scenario.** Run as documented: an untracked `mindweave-source-1ba95c9.zip` appears in the worktree root. `git status` is now dirty (rule 1 'occupied'), and the next `scripts/windows.sh sync` uploads it, because line 59 there sends untracked non-ignored files, adding a megabyte to a link TRACK.md says drops long transfers. If a check fails, a zip with the normal name remains and can be submitted by mistake since nothing marks it as rejected.

**Recommendation.** Default OUT to a directory already ignored (for example `artifacts/`) or add `mindweave-source-*.zip` to .gitignore; build to a temp name and rename only when all checks pass, deleting on failure; widen the pattern to `(^|/)\.env(\..*)?$` plus `.pem`; quote `$bad` (`printf '%s\n' "$bad" | sed 's/^/  /'`). Decide explicitly, via export-ignore, whether agent files and review archives belong in the delivery.

**Skeptic: partially confirmed, low.** Every factual claim checks out against the code, but the consequences are minor and self-inflicted, so medium is overstated.

**Confirmed as stated**
- **Zip lands in the worktree.** `OUT` defaults to `$PWD/mindweave-source-<sha>.zip`, the run guide runs the script bare from the repo root, and `.gitignore` has no zip entry. The result is an untracked, non-ignored file.
- **Sync uploads it.** `scripts/windows.sh` `sync_once` builds its list with `git ls-files --cached --others --exclude-standard`, so the zip would be sent. It is about 1.07 MB (stage-reports.md:529) and already compressed, against a roughly 320 KB gzip payload on a link TRACK.md:151 records as dropping a 1.4 MB transfer.
- **Zip survives a failed check.** The archive is written at line 16 before any check, and line 50 exits with `$fail` without removing it; there is no trap.
- **Pattern gaps.** The deny pattern ends in `\.env$` and has no `.pem`, `.zip` or log entry.
- **Unquoted `$bad`.** Line 31 is unquoted as claimed.
- **No export-ignore.** `.gitattributes` holds only `* text=auto eol=lf`. `.keep` is tracked, and files under `docs/reviews` contain `/Users/` paths.

**Why the severity is low**
- **Not silent.** A failed run prints `FAIL ...` to stderr and exits non-zero; the leftover zip is a tidiness issue.
- **Trivially recoverable.** The stray zip is visible in `git status` and removed with one `rm`. Nothing breaks: sync retries and verifies file counts.
- **Pattern gaps are second-line only.** `git archive` packs tracked files only, and `.gitignore` already blocks `.env` and `.env.*`. No `.env`, `.pem` … (cut; full text in the workflow journal)

*Corrections to the finding.* - Severity should be low, not medium: no data loss, no secret leak with the current tree, and nothing silent.
- "Nothing marks it as rejected" is overstated: the script prints FAIL lines and exits non-zero; only the leftover file itself is unmarked.
- The `.env.local` gap is defence in depth only, since `.gitignore` already ignores `.env.*` and `git archive` packs tracked files only. `prod.env.bak` is a real gap in both layers but hypothetical.
- The unquoted `$bad` only garbles the diagnostic listing; detection and exit code are unaffected.
- Shipping AGENTS.md, CLAUDE.md, TRACK.md, .github/ and docs/reviews is an undecided policy question, not a defect; the run guide deliberately points readers to TRACK.md. The `/Users/` paths expose only the local username.
- The sync cost is better stated as roughly quadrupling the payload (about 320 KB to about 1.4 MB, since a zip does not recompres … (cut; full text in the workflow journal)

### repo-scripts-6. CI has no token permissions block, floating action tags, and no package lock

- Final: **low** (confirmed in part); auditor said medium
- Where: `.github/workflows/ci.yml:31`; effort M

**Evidence.** ci.yml and stress.yml contain no `permissions:` key, so jobs get the repository default GITHUB_TOKEN scope. Lines 31-32 use `actions/checkout@v4` and `actions/setup-dotnet@v4` (mutable tags). There is no packages.lock.json and no RestorePackagesWithLockFile (grep finds neither), and line 35 installs `10.0.x` while global.json uses rollForward latestFeature, so SDK and transitive package versions float between runs. stress.yml:49-52 interpolates `${{ inputs.filter }}` and `${{ inputs.project }}` directly into a pwsh script. macOS, one of the two target platforms, runs only on manual dispatch (ci.yml:24); TRACK.md line 48 records this as a cost decision.

**Failure scenario.** A transitive NuGet dependency or the SDK feature band changes between two pushes of identical source: CI turns red (warnings are errors) or, worse, green with different binaries than the local machines, with no diff to explain it. A compromised or re-tagged v4 action runs with whatever write scope the repo default grants. A dispatch of stress.yml with a filter containing `"; <command>; "` executes arbitrary PowerShell on the runner (limited to collaborators).

**Recommendation.** Add `permissions: contents: read` to both workflows; pin actions to commit SHAs; enable RestorePackagesWithLockFile with `dotnet restore --locked-mode` in CI; pass workflow inputs through `env:` instead of inline interpolation. Keep the macOS-on-dispatch decision but note it as the reason macOS regressions are found only locally.

**Skeptic: partially confirmed, low.** The literal observations are accurate, but the three failure scenarios are each weaker than claimed, so this is a low-severity hardening item, not medium.

**What holds**
- Neither workflow has a `permissions:` key.
- Both use the mutable tags `actions/checkout@v4` and `actions/setup-dotnet@v4`.
- There is no `packages.lock.json`, no `RestorePackagesWithLockFile`, no `nuget.config`, and no dependabot/renovate config.
- CI installs `10.0.x` and `global.json` uses `latestFeature`.
- `stress.yml` interpolates `inputs.*` directly into shell.
- macOS runs only on manual dispatch.

**Why the scenarios are overstated**
1. **Token scope.** The repository default is already read-only: the GitHub API returns `default_workflow_permissions: "read"` and `can_approve_pull_request_reviews: false`. A compromised or re-tagged action would not get write scope. The workflows also reference no secrets. An explicit `permissions:` block would only guard against someone changing the repo setting later.
2. **Package drift.** All 15 direct dependencies are pinned to exact versions through central package management in `Directory.Packages.props`, with no floating or range versions. NuGet resolves transitive dependencies to the lowest applicable version, so identical source restores the same graph on every run. "A transitive dependency changes between two pushes of identical source" does not happen in normal operation. A lock file would add content-hash verification and protection against a package being replaced on the feed, which is a narrower benefit.
3. **SDK drift.** This is real, but it is a re … (cut; full text in the workflow journal)

*Corrections to the finding.* - **Token scope:** "jobs get the repository default GITHUB_TOKEN scope" is true, but that default is read-only, so the "whatever write scope the repo default grants" scenario grants none.
- **Transitive packages:** the claim that transitive package versions float between runs is wrong. All direct versions are exact-pinned in `Directory.Packages.props`, and NuGet's lowest-applicable resolution is deterministic without a lock file. Only the SDK feature band floats, and that is a documented decision (TRACK.md line 126).
- **Line numbers:** the `stress.yml` interpolations are at lines 41, 45, 50, 51 and 58, not 49-52. The finding also misses `runs-on: ${{ inputs.os }}` at line 30.
- **Injection:** "arbitrary PowerShell on the runner (limited to collaborators)" is not an escalation. The private repo has one collaborator, dispatch needs write access, the token is read-only and no secrets are u … (cut; full text in the workflow journal)

### repo-scripts-7. windows.sh sync runs with errexit silently disabled and misreports a failed remote clean-up as a transfer failure

- Final: **low** (not verified); auditor said low
- Where: `scripts/windows.sh:49`; effort S

**Evidence.** Line 49 calls `retry_ssh sync_once`, and retry_ssh runs it as `"$@" && return 0` (line 31). In bash, a function invoked on the left of `&&` runs with `set -e` ignored for its whole body, so a failure of the clean-up `remote ...Remove-Item -Recurse -Force '$SRC'...` on line 55 or of `git ls-files` on line 59 does not stop sync_once. Line 64 maps any failure of the local tar pipe to 255 (`|| return 255`), which retry_ssh treats as a connection failure. The comment on line 53 says build output lives outside the snapshot, but no ArtifactsPath or BaseOutputPath is set anywhere, so `dotnet test` on line 80 writes bin/ and obj/ inside $SRC.

**Failure scenario.** A lingering testhost or MSBuild node on Windows holds a DLL under $SRC/tests/*/bin. Remove-Item throws, sync_once continues, the tarball is extracted over the leftover tree, the count on line 66 is higher than expected, and the script uploads the whole tree six times over the slow relayed link before failing with `sync incomplete: N of M files arrived`, which points at the network rather than the locked file. Every sync also discards obj/, forcing a full restore and rebuild on each Windows test run.

**Recommendation.** Make sync_once fail fast explicitly (`remote ... || return $?` after the clean-up, `files=... || return 1`), return 255 only when ssh itself exits 255 (check PIPESTATUS), and print the Remove-Item error. Either correct the comment or move build output outside the snapshot with ArtifactsPath so restores survive a sync.

### repo-scripts-8. mailbox.sh wait swallows every remote error and reports an unreachable machine as 'no reply yet'

- Final: **low** (not verified); auditor said low
- Where: `scripts/mailbox.sh:104`; effort S

**Evidence.** Line 104: `found="$(remote "..." | tr -d '\r' || true)"` discards the exit status of `remote`, including 255 after six failed connection attempts and PowerShell errors such as a missing to-mac folder. The loop then sleeps 60 s (line 107) until the deadline and prints `no reply to $1 yet` (line 106). Related: line 76 builds the id from a one-second timestamp plus slug, and line 52 moves it into place with `Move-Item -Force`, so a second send in the same second with the same slug replaces the first; `retry` (lines 29-39) also re-runs the non-idempotent Move-Item on lines 52 and 95 after a dropped connection, reporting failure for an operation that succeeded.

**Failure scenario.** The SOCKS proxy on 127.0.0.1:7898 is down or the Windows laptop is asleep. `scripts/mailbox.sh wait <id>` runs in the background for an hour as docs/mailbox.md:56 suggests, then exits 1 with 'no reply to ... yet'. The agent concludes Windows Claude has not answered, when in fact nothing was ever checked; the connection errors went to stderr of a background job.

**Recommendation.** In `wait`, capture the status and exit immediately with a distinct code on non-255 errors, and after N consecutive connection failures report 'mailbox unreachable'. Drop `-Force` on line 52 so id collisions fail loudly, and make `done` treat 'already in done/' as success.

### repo-scripts-9. Personal paths, account identifiers and the test machine's layout are committed and already pushed

- Final: **low** (not verified); auditor said low
- Where: `docs/architecture.md:40`; effort M

**Evidence.** docs/architecture.md:40 links to `/Users/<user>/School/Courses/2026-Fall/.../期末项目与报告要求.docx`. `git grep` counts the home path in 20 tracked files, including 49 occurrences in docs/reviews/review-20261023-023314-sgt/evidence/baseline-manifest.json and 48 in final-integrity.json. All 427 commits carry a numeric QQ mailbox address as author email. prototypes/responses-compat/results/*.json record the third-party relay host used for API access; scripts and TRACK.md name the SSH host alias, SOCKS port and E:\ directories. No credentials are among them.

**Failure scenario.** TRACK.md line 48 mentions switching the repository to public to get free CI minutes. Doing so publishes the user's OS account name, university course directory, personal mailbox number and API reseller in every clone, and history rewriting would then be the only remedy. Independently, the absolute link in the authoritative architecture document is dead for every reader other than this Mac, including the course marker reading the source package.

**Recommendation.** Before any visibility change, decide whether to rewrite history (author email, evidence manifests) or accept the exposure. Regardless, replace the absolute link in docs/architecture.md on main with a description or a copy of the requirement text, and set a noreply commit email going forward.

### repo-scripts-10. real-api.sh accepts an unvalidated profile name and a loose plain-http exception

- Final: **low** (not verified); auditor said low
- Where: `scripts/real-api.sh:19`; effort S

**Evidence.** Line 19: `PROFILE="${2:-default}"` is used unchecked in `rm -f "$CONFIG_DIR/$PROFILE.env"` (line 54), in `source "$CONFIG_DIR/$PROFILE.env"` (line 44) and as the redirect target on line 38. Line 32 allows `http://localhost*` and `http://127.0.0.1*` by prefix glob, which also matches `http://localhost.example.com` or `http://127.0.0.1.nip.io`. Also, .keep (empty, added in the initial baseline 888e321) is still tracked at the repo root although AGENTS.md says empty directories are not pre-created, and both prototype package-macos.sh files are mode 100644 while the product scripts are 100755.

**Failure scenario.** `scripts/real-api.sh remove ../../other` deletes `~/.config/other.env`; `scripts/real-api.sh test ../../x` sources an arbitrary `.env` file as shell code. A mistyped base URL such as `http://localhost.internal-proxy.example` passes the https check and the real API key is then sent in clear text by the test run.

**Recommendation.** Validate PROFILE against `^[A-Za-z0-9_-]+$` before use; match the plain-http exception with a regex that requires `:`, `/` or end of string after the host (`^http://(localhost|127\.0\.0\.1)(:[0-9]+)?(/|$)`). Remove the root .keep.

