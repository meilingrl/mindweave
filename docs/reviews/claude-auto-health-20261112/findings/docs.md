# Documentation health — grade C

The newest layer of documentation is accurate and unusually honest: the latest stage report's test counts (91+8+412+381 = 892 passed, 18+58 = 76 skipped), schema v10, 55 gallery scenes and perf budgets all match the code and the baseline test output, every relative link in the key docs resolves, and mailbox.md matches both scripts. But the documentation is an append-only log, not a maintained description of the system. Older design docs (data-model.md, decisions.md, s5-network.md, s6-windows-checklist.md, run-guide.md) were never reconciled with later changes and now contradict the code on deletion rules, layout parameters and backup/restore. TRACK.md, declared the single progress entry, has a 5,028-character status bullet with stacked history, internal contradictions, and a "next step" that HEAD has already overtaken. Design for data-safety features (in-app restore, migration v10, statistics) sits as sub-sections of a 248 KB "UI polish" file that the design index does not list. The shared requirements no longer describe several shipped features and contradict themselves on deletion. A new contributor cannot work out the current design without reading about 400 KB of chronological notes or the code. That is below an industrial bar, though the raw information is almost all present and truthful as of when it was written.

**Strengths**

- Stage reports are verifiable and match reality. docs/design/stage-reports.md (item 6) claims 'Core 91、Architecture 8、Infrastructure 412＋18 跳过、Desktop 381＋58 跳过'; the baseline test.txt shows Desktop 'Passed: 381, Skipped: 58'; TRACK's '892 通过、76 跳过' is the exact sum; '画廊 55 场景' matches 55 G-methods in tests/Mindweave.Desktop.Tests/GalleryTests.cs; 'v10' matches SchemaMigrations.cs.
- Every stage report has an explicit '还没验证 / 未做' section (for example real-window behaviour, screen readers, Windows, half-hour time zones), so unverified claims are separated from verified ones.
- No broken relative links in TRACK.md, README.md, docs/design/README.md, run-guide.md, stage-reports.md, decisions.md, evolution-seams.md, mailbox.md, development-routes.md or evolution.md (checked every relative markdown link against the file system).
- docs/mailbox.md matches scripts/mailbox.sh and scripts/mailbox.ps1 exactly: folders, id format (regex ^[0-9]{8}-[0-9]{6}-[mw]-...), kinds, dot-temp-then-rename, command names and flags. It also has a clear trust section saying messages are not user consent.
- In-code doc comments are dense, current and explain why: for example SqliteBackupFiles.Prune explains why only migration backups rotate, NetworkLayout constants record 'S7 round-1 fix: 170 → 100', and SourceAnchors.CompatibleProfiles explains math-1/2/3 compatibility. There are zero TODO/FIXME/HACK markers in src or tests.
- Decision constants that were not later changed still match code: D-011 PerMessage 4 / PerRequest 16 (ContextBudget.cs), D-015 16,384 (ContextCompressor.cs:46), D-017 0.9 and 8 s (SummaryRules.cs), D-010 reading line 8 px (ConversationView.axaml.cs:22), D-005 five migration backups (SqliteDatabase.cs:26), title cap 1024 (ConversationTitler.cs:26), D-013 (no stream_options anywhere in src).
- TRACK.md findings are high-value engineering knowledge with cause, evidence and fix (for example SQLITE_IOERR_TRUNCATE on Windows after a kill, the UUIDv7 same-millisecond ordering, PowerShell stderr aborting the pipeline with exit 0).
- Script headers (windows.sh, real-api.sh, perf.sh, package-source.sh, mailbox.sh) document usage, environment variables and safety properties, and package-source.sh enforces that run-guide.md ships in the source package.

**Not examined**

- docs/architecture.md and docs/requirements.md were only spot-checked (headers, §5.2, §8.2, §10.3, §12.2, revision log); no section-by-section comparison with the code.
- docs/design/s1-chat.md, s2-branching.md, s3-context.md, s4-summaries.md, s6-wrapup.md, s7-prototype-visual-language.md and evolution-seams.md were not compared line by line with the code; only targeted greps (calibration, PerfSeed/SeamTests existence, deletion wording).
- s7-ui-polish.md (248 KB) was sampled by headings and greps, not read in full.
- docs/history/, docs/reviews/ and the prototypes' READMEs.
- The older stage reports (S0–S7) were not re-verified against code; only the latest (item 6) was.
- XML doc comment coverage across the public API was not measured (GenerateDocumentationFile is false, so the compiler does not check cref targets); comments were sampled, not exhaustively checked.
- Whether the commands in run-guide.md and the scripts actually run (no dotnet commands, no SSH and no network were used, per the audit rules).
- The Windows machine's mailbox contents, E:\mindweave-reports and the Windows Claude's instructions (not reachable read-only).
- The other track's TRACK.md and docs (track/codex-steady).

### docs-1. TRACK.md 'next step' is behind HEAD: requirements v0.13 is already drafted and merged, but TRACK still says to draft it

- Final: **medium** (confirmed (one verifier lowered it)); auditor said high
- Where: `TRACK.md:31`; effort S

**Evidence.** TRACK.md:31 says '下一步：第 7 项（推荐问题与推荐节点），先在 `main` 起草需求更新，等你确认后再实现' and '`main` 的两个文档提交（v0.12 需求、D7 方向）你 2026-11-10 决定暂不推送'. But HEAD is '1ba95c9 Merge main (requirements v0.13) into track/claude-auto' (2026-11-12 19:22), merging '1086f87 Requirements v0.13: recommended questions and nodes, user-made and confirmed summary links'. docs/requirements.md is 'v0.13 · 2026-11-12' with a new §10.3. 'grep 0.13 TRACK.md docs/design/*.md' finds no mention of v0.13. The last TRACK commit is da4c804 (16:13), before both. Local main is 3 commits ahead of origin/main (bf4b549, 1021b69, 1086f87), not two, and origin/track/claude-auto == HEAD already carries all three through merges. TRACK.md:13 promises '每次有意义的提交都同步更新“当前状态”与“接续点”' and :16 '任何新会话只需读本文件“接续点”'.

**Failure scenario.** A new session follows the protocol, reads only 接续点 and starts 'drafting the requirements update on main' for item 7 again. That duplicates or conflicts with §10.3 already on main, or it waits for a user confirmation whose status is recorded nowhere. Nothing says whether the user confirmed v0.13, so a session could equally start implementing item 7 against an unconfirmed draft. The 'two unpushed commits' statement also misleads any decision about pushing main.

**Recommendation.** Update TRACK.md 当前状态/接续点 for the v0.13 merge: say v0.13 §10.3 is drafted on main and merged into the track, state explicitly whether the user has confirmed it, and make the next step 'detailed design of item 7 against §10.3' or 'waiting for confirmation'. Correct the unpushed-commit count to three and note that origin/track/claude-auto already contains them. Treat a merge from main as a 'meaningful commit' that requires a TRACK update.

**Skeptic: partially confirmed, low.** Every factual claim checks out, but the failure scenario is largely defused by repository state the auditor did not look at, so "high" is overstated.

What is true:
- TRACK.md:31 still says the next step is item 7, "先在 `main` 起草需求更新，等你确认后再实现", and that `main` has two unpushed doc commits (v0.12, D7).
- The track tip is 1ba95c9 "Merge main (requirements v0.13) into track/claude-auto" (2026-11-12 19:22). It merges 1086f87 (17:09), which adds requirements §10.3 (v0.13).
- TRACK.md was last touched by da4c804 (16:13), before both. Nothing in TRACK.md or docs/design/*.md mentions v0.13.
- Local main is three commits ahead of origin/main (bf4b549, 1021b69, 1086f87), not two. origin/track/claude-auto equals the track tip, so all three are already on the remote via the track branch.
- TRACK.md:13 and :16 do promise that every meaningful commit updates 当前状态/接续点 and that a new session reads only 接续点.

What weakens it:
- Item 7 is already being implemented in a task worktree. Branch worktree-wf_e0cb2e5e-84d-1 has c75c215 "Recommended questions ... (item 7a, v11)" (20:29) and 3558693 "Recommended nodes ... (item 7b)" (21:24), with uncommitted edits still in progress.
- AGENTS.md parallel rule 3 says TRACK.md is updated only when a task branch merges back, and the task branch must not edit it. So TRACK lagging while item 7 is in flight is the designed behaviour, not neglect; it will be rewritten at the item 7 merge.
- AGENTS.md parallel rule 1 requires a new session to check `git worktree list` before writing. That would show the item 7 worktree, so "re-draft the requirements on main" i … (cut; full text in the workflow journal)

*Corrections to the finding.* - Severity should be low, not high: item 7 is already under implementation in task worktree wf_e0cb2e5e-84d-1 (commits c75c215, 3558693), and AGENTS.md rule 3 defers TRACK.md updates to the merge back.
- The scenario "a new session re-drafts requirements on main" ignores the mandatory `git worktree list` check (AGENTS.md rule 1) and the fact that §10.3 is visible on opening the file.
- "Nothing says whether the user confirmed v0.13" is slightly overstated: §10.3 records the user's 2026-11-10 decision to add these features. What is unrecorded is confirmation of the drafted v0.13 text.
- The finding's factual claims (line 31 text, commit times, three unpushed commits, origin/track/claude-auto already carrying them, no v0.13 mention) are all accurate.

**Reproduction attempt: confirmed, medium.** This is a documentation/process finding, so no xUnit test applies; I verified it with read-only git commands from my isolated worktree against the track/claude-auto ref, plus one read-only grep of the live TRACK.md.

Every factual claim checks out:
- track/claude-auto HEAD is 1ba95c9 "Merge main (requirements v0.13) into track/claude-auto" (2026-11-12 19:22), which merges 1086f87 "Requirements v0.13: recommended questions and nodes..." (17:09).
- docs/requirements.md on the track is v0.13, updated 2026-11-12, and references §10.3.1–10.3.4.
- The last commit touching TRACK.md is da4c804 / 1e6da26 at 16:13, before both.
- TRACK.md line 31 still says the next step is item 7, "先在 `main` 起草需求更新，等你确认后再实现", and that main has two unpushed doc commits (v0.12, D7).
- TRACK.md has no mention of v0.13 or §10.3; the only "0.13" hit is the unrelated runtime version 10.0.13 on line 130. docs/design/*.md likewise only has 10.0.13. The live (uncommitted) TRACK.md in the shared checkout is the same.
- origin/main..main is three commits (bf4b549, 1021b69, 1086f87), not two.
- origin/track/claude-auto equals local track HEAD and contains main, so all three "unpushed" main commits are already on the remote through the track branch.
- TRACK.md line 13 promises both sections are updated with every meaningful commit, and line 16 says a new session needs only 接续点.

Nothing in the repo records whether the user confirmed v0.13. The merge into the track hints at it but is not a record.

I lowered severity from high to medium. The staleness is about three hours and one merge old, with no product code a … (cut; full text in the workflow journal)

*Corrections to the finding.* The facts in the finding are accurate; two points are overstated or need context.

- Severity: high is too strong. The lag is one merge commit and about three hours, and any session that checks main or git log before drafting sees "Requirements v0.13" at once. Duplicate drafting on main is unlikely. The real gaps are the unrecorded confirmation status and the stale push statement.
- The "two unpushed commits" sentence was correct when written on 2026-11-10 (v0.12 and D7). It became stale when 1086f87 was added. The more material point is that all three commits are already on origin through origin/track/claude-auto, so "暂不推送" no longer describes what is on the remote, even though origin/main itself is still behind.

<details><summary>Reproduction</summary>

Not test-reproducible (documentation/process finding). Verified by read-only git inspection; the commands and their outputs are in the evidence field. No files were added or modified; `git status --short` in the worktree is empty.

</details>

### docs-2. TRACK.md is no longer usable as the single progress entry: status and resume points are stacked history with stale and contradictory statements

- Final: **low** (confirmed in part); auditor said medium
- Where: `TRACK.md:21`; effort M

**Evidence.** Line 21 (当前状态 › 阶段) is one bullet of 5,028 characters chaining about 17 '此前 …' clauses back to S7; line 23 (测试) is 2,668 characters with eight historical test counts; lines 30–31 (接续点) are 3,219 and 2,502 characters; '此前' occurs 40 times in the file. Stale statements sit beside current ones: (a) line 31 ends '--filter Category=Gallery`（39 个场景）' while line 23 says '各 55 个场景' and the code has 55; (b) line 23 ends '迁移到 v8' and line 22 '未改迁移（仍为 v8）' while the same line 23 says '数据库 v10'; (c) line 86 '仍可选：应用打开时从备份恢复的入口；删除前的备份会累积、应用里没有清理入口' contradicts line 76 '5.（已合并 2026-11-12）从备份恢复与备份清理', and line 31 still says '可选项还剩“从备份恢复”与“备份清理”'; (d) line 21 ends 'Windows 试用版：`E:\mindweave-claude-auto\app-fix\Mindweave.exe`' and the '需要你' table (line 44) asks the user to try app-fix, a 2026-11-05 build that predates items 1–6; (e) '需要你' says '当前没有阻塞项' while line 31 holds three separate asks of the user (view item 6 screenshots, decide two 待决定 items, try a restore on macOS).

**Failure scenario.** A reader, human or agent, has to parse a 5 KB paragraph to learn 'what is the state now', and can pick up a superseded fact: runs the gallery expecting 39 scenes, assumes schema v8, proposes 'backup restore' as remaining optional work, or tells the user to validate on app-fix as if it were the current build. The user's pending asks are buried in a '此前' chain and get missed.

**Recommendation.** Restructure TRACK.md: keep 当前状态 and 接续点 to current facts only (one line each: stage, schema version, last full-suite numbers with date and commit, latest Windows build and what it contains, next step). Move every '此前' clause to a dated changelog section or rely on stage-reports.md, which already holds it. Put all open asks in the '需要你' table. Delete or strike resolved '仍可选' text. Give the gallery command without a scene count.

**Skeptic: partially confirmed, low.** Every quoted fact checks out, but the "contradictory statements" framing is overstated, so I put this at low rather than medium.

What holds:
- TRACK.md line lengths are exactly as claimed (5,028 / 2,668 / 3,219 / 2,502 characters for lines 21, 23, 30, 31), and '此前' occurs 40 times.
- TRACK.md:16-19 declares the file the only progress entry and says a new session need only read 接续点. That section is about 5.7k characters, mostly history, so the maintainability problem is real.
- The gallery command at the end of line 31 still says 39 scenes, with no date beside it; line 23 says 55.
- The 需要你 table's only row asks the user to try `app-fix`, a build line 30 dates to the 2026-11-05 fix round, before items 1-6 merged on 11-10 to 11-12.
- The current asks (view item 6 screenshots, two 待决定 decisions) appear only in line 31, not in the 需要你 table.
- Line 86 still lists backup restore and cleanup as '仍可选' although line 76 marks item 5 merged.

What is overstated:
- Each long bullet leads with the current fact. Line 21 opens with item 6 merged, line 23 with 892 passed / v10 / 55 scenes, line 31 with the item 7 next step and the current asks. A reader who stops at the first sentence gets the right state.
- The v8 statements are dated history, not contradictions. Line 22 is a 2026-11-01 entry ("did not change migrations, still v8"); line 23's '迁移到 v8' closes the S5-era clause.
- Line 86 sits under a bullet marked resolved 2026-11-04, and line 31's "optional items remaining" follows a '此前下一步' marker. Both are stale but flagged as past.
- '当前没有阻塞项' is literally true: the asks in line 31 a … (cut; full text in the workflow journal)

*Corrections to the finding.* - (b) is not a contradiction. Both v8 statements are dated history: line 22 is the 2026-11-01 A1-A6 entry, and line 23's "迁移到 v8" closes the S5-era clause. The current fact (v10) leads line 23.
- (c) is weaker than stated. Line 86 is inside a bullet marked resolved 2026-11-04, and line 31's "可选项还剩" follows a "此前下一步" marker. They are stale leftovers, not statements presented as current.
- (e) is overstated. "当前没有阻塞项" is literally true because the asks are non-blocking, and the macOS restore trial is behind "此前下一步". The valid point is narrower: the open non-blocking asks are missing from the 需要你 table, whose only row is the outdated `app-fix` trial.
- "The code has 55" gallery scenes was not confirmed; only TRACK.md's own 55-vs-39 inconsistency was.
- The failure scenario is overstated: each bullet opens with the current fact, so a reader has to go deep into the "此前" tail to pick up a supe … (cut; full text in the workflow journal)

### docs-3. Windows acceptance checklist expects the pre-v0.12 behaviour (refuse deleting a conversation with branches or summaries), yet TRACK still offers it for acceptance

- Final: **low** (confirmed in part); auditor said medium
- Where: `docs/design/s6-windows-checklist.md:47`; effort S

**Evidence.** Checklist row 25: '删除一个有子分支或总结的会话；再删除一个没有依赖的会话 | 前者拒绝并说明原因、提供归档；后者确认后删除'. Requirements §8.2 (v0.12): '带子分支或总结的会话，删除时连同它的全部子分支，以及这些会话的总结一起删除；确认框列出…数量，并提供“改为归档”'. The code does the cascade: SqliteArchiveStore.DeleteScope 'The union of each existing selected conversation's subtree with the summary nodes of its members' and deletes summary_node/summary_version/conversation per member (SqliteArchiveStore.cs:79–147). Row 23 says '升级 v8 时生成了备份' (schema is v10) and line 5 says to run 'app-s6\Mindweave.exe'. The file was last changed 2026-10-28. TRACK.md:44 still points to it: '想逐项核对时用 [验收清单](docs/design/s6-windows-checklist.md)'. The checklist has no rows for multi-select delete, workspace delete, theme switch, node dragging, in-app restore, statistics, or notifications.

**Failure scenario.** The user or the Windows Claude tester walks the checklist on a current build. Row 25 shows a scope dialog and deletes the subtree; the checklist says it must refuse, so correct behaviour is marked '不通过' (or an agent files a bug or 'fixes' the product back). Everything added since S6, including destructive operations (workspace delete, restore from backup), gets no real-machine acceptance row at all.

**Recommendation.** Revise the checklist to current behaviour: row 25 per §8.2/§11 (scope counts, 改为归档, backup first), row 23 version-neutral, and the build path taken from TRACK. Add rows for multi-select archive/delete, workspace delete with name confirmation, restore from backup and backup deletion, theme switch, node drag and save, new-answer toast, statistics. Or mark the file as the frozen S6 checklist and stop linking it from '需要你'.

**Skeptic: partially confirmed, low.** The core claim holds: checklist row 25 expects the pre-v0.12 behaviour, and TRACK.md still links the file from the "需要你" table. I could not find any marker freezing the checklist as S6-only, nor a newer checklist that replaces it.

What I verified:
- Row 25 says deleting a conversation with sub-branches or summaries is refused with archive offered. Requirements §8.2 and the §11 reliability row now require a cascade delete with scope counts, "改为归档" and backup first.
- The code implements the v0.12 behaviour (ConversationDeletionViewModel cites "requirements v0.12 §8.2"; ConversationDeleteScope and the "改为归档" string exist).
- The checklist has one commit only (4050fa7, 2026-10-28) and no rows for multi-select delete, workspace delete, restore, theme, statistics or notifications.

Why the severity is overstated:
- The TRACK row is explicitly "(可选，不阻塞)", and TRACK.md:40 says Windows real-machine testing is deferred by the user's decision of 2026-11-05.
- The same TRACK line records that the user already tried the new delete build (`app-del`) on the Windows desktop and confirmed it on 2026-11-05, so the changed delete flow did get real-machine review outside the checklist.
- The stale path is hedged in the checklist itself ("发布后的路径以阶段报告为准"), and TRACK names the current build (`app-fix`).
- Row 23's "升级 v8" is stale wording at most; a pre-migration backup is still the expected result.
- The "agent fixes the product back" scenario is speculative: AGENTS.md makes requirements.md authoritative, and the code comments cite v0.12.
- The checklist was scoped as "one row per §11 scenario … (cut; full text in the workflow journal)

*Corrections to the finding.* - Severity should be low, not medium: the link is optional and non-blocking, Windows testing is deferred, and the user already accepted the new delete flow on a real Windows build (`app-del`, 2026-11-05).
- The stale `app-s6` path is hedged by the checklist's own caveat, and TRACK gives the current path.
- I did not verify the "schema is v10" claim or the cited SqliteArchiveStore.cs lines 79-147. The file is at src/Mindweave.Infrastructure/Sqlite/SqliteArchiveStore.cs; s7-ui-polish.md:366 says the delete-multi round was still at v8.
- "No real-machine acceptance at all" for post-S6 features is overstated for conversation delete; it stands for the other features only as far as this checklist goes.
- The "agent reverts the product" scenario is speculative, given that requirements.md is authoritative.

### docs-4. data-model.md states deletion rules the code no longer follows, stops at S6, and there is no current schema reference for 10 migrations

- Final: **low** (confirmed in part); auditor said medium
- Where: `docs/design/data-model.md:38`; effort M

**Evidence.** data-model.md:38 '会话删除只在无子会话、无总结节点、无用户任务时允许'; :33 lists the transaction '无依赖会话删除；归档与恢复 | S6'; :15 '删除检查只看本会话自身关系'. Code: SqliteArchiveStore deletes a whole subtree with its summary nodes in one transaction; SqliteWorkspaceStore.cs:112–124 deletes an entire workspace across 12 tables. Neither is in the doc's transaction table. The doc's last commit is 2026-10-25 and it says nothing about message_block, ai_request (v3; rebuilt in v10 with nullable task_id and workspace_id ON DELETE CASCADE), context_summary (v5), conversation_archive (v8), estimated_tokens (v9), restore, or the rule that deleting a conversation or workspace deletes its ai_request usage rows (TRACK 待决定 (a)). docs/design/README.md:7 still advertises it as '稳定身份、来源依赖…事务与删除边界'. The same residue is in code: SummaryExportService.cs:119 'a conversation with a summary cannot be deleted' (it can since v0.12; it is deleted together with its summary).

**Failure scenario.** A contributor adding the v0.13 tables (recommendations, content links) reads the data-model doc to learn the deletion contract and concludes a conversation with a summary node can never be deleted. They then omit the new tables from the cascade steps in SqliteArchiveStore.MemberSteps and SqliteWorkspaceStore. With RESTRICT foreign keys this makes every cascade delete or workspace delete fail at runtime; with CASCADE it silently removes rows the confirmation dialog never counted, which violates §8.2 '不做用户没有看到范围的连带删除'.

**Recommendation.** Rewrite the 删除边界 and 事务边界 sections to as-built: three delete kinds, scope computed and re-checked in the transaction, backup first, and the explicit ordered list of tables each delete touches, with the rule 'a new table referencing conversation/summary_node/workspace must be added here and in both delete paths'. Add a short per-migration table (v1–v10: tables and columns added, owning stage doc). Fix the comment at SummaryExportService.cs:119.

**Skeptic: partially confirmed, low.** The staleness is real, but the claimed failure scenario is largely mitigated and one sub-claim is overstated, so this is low, not medium.

What holds:
- docs/design/data-model.md has a single commit (6470e8e, 2026-10-25, S0) and has never been updated. Line 38 still says a conversation can be deleted only with no child conversations, no summary nodes and no user tasks. Line 33 lists only "无依赖会话删除；归档与恢复 | S6". Line 15 says the delete check looks only at the conversation's own relations.
- The code contradicts this. SqliteArchiveStore.DeleteAsync deletes a whole scope (selected conversations, all descendants, their summary nodes, versions, drafts and layout rows) in one transaction, after recomputing the scope. SqliteWorkspaceStore deletes an entire workspace across about 12 tables.
- Requirements v0.12 (docs/requirements.md:450) changed the rule from "only dependency-free conversations" to "state the scope, then delete". data-model.md was not updated.
- docs/design/README.md:7 still advertises the doc as covering "事务与删除边界".
- The doc does not mention message_block, ai_request, context_summary, conversation_archive, or the v9/v10 changes.
- The comment at SummaryExportService.cs:119 ("a conversation with a summary cannot be deleted") is stale. It is comment-only: the code below it handles a missing conversation via FirstOrDefault.

What is overstated:
1. "There is no current schema reference for 10 migrations" is wrong as a defect claim. data-model.md:3 says by design that it fixes only identities and boundaries and that concrete fields land with each stage's migration. Each … (cut; full text in the workflow journal)

*Corrections to the finding.* - "No current schema reference for 10 migrations" is overstated. Each migration is documented in its stage doc (s2-branching, s3-context, s4-summaries, s6-wrapup, stage-reports for v9/v10), and data-model.md:3 explicitly declines to be a schema reference. Only a consolidated index is missing.
- The finding omits that the as-built cascade-delete contract (scope, in-transaction recheck, ordered table list, backup first) is already written in docs/design/s7-ui-polish.md:364-370 and decisions.md D-024, and is linked from TRACK.md. The defect is a stale, contradicting data-model.md and a README that does not route to the current text.
- The runtime failure scenario is mostly caught. A RESTRICT omission fails ArchiveFlowTests and WorkspaceDeleteFlowTests (all-table TEXT-column residue scan plus foreign_key_check), provided the fixture populates the new table. Both delete paths also carry in-co … (cut; full text in the workflow journal)

### docs-5. Decision record and S5 design state superseded values as current; decisions.md stops at D-024 (2026-11-03)

- Final: **low** (confirmed in part); auditor said medium
- Where: `docs/design/decisions.md:85`; effort M

**Evidence.** D-019 (decisions.md:85): '布局参数：层间距 170、群组留白 160、碰撞修正 40 轮、缩放 0.25–2.5（适配时不超过 1.5）、适配边距 48'; s5-network.md:56–60 has the same table (170 / 160 / 48). Code: NetworkLayout.cs 'LevelSpacing = 100' ('S7 round-1 fix: 170 → 100'), 'GroupGap = 72' ('160 → 72'); NetworkSession.cs 'FitMargin = 32'. The change is recorded only in s7-ui-polish.md:83. D-024 item 1 (decisions.md:133): '这与需求 §6.1 第 1 条…不一致…需由你决定是否更正 main 的需求' and '应用打开时没有从备份恢复的入口（新功能，未做）'; requirements v0.12 now allows workspace deletion and item 5 shipped restore. D-014/D-016 say the render profile is 'math-2'; the code is 'livemarkdown-2.5.1/math-3' (SourceAnchors.cs:64), documented only in s7-ui-polish.md:111. D-011 describes a fixed, deliberately high estimator with no pointer to the per-model calibration that replaced the '10% 余量' assumption (s3-context.md:17, migration v9). No D-entries exist for anything after 2026-11-03: cascade-delete design, token calibration, thinking state, new-answer notifications, workspace-name background requests (default on, costs tokens), in-app restore, statistics semantics. No grep hit for '已取代/superseded/见 s7' in s0–s6 docs, decisions.md or data-model.md.

**Failure scenario.** Someone tuning the network layout, or writing a layout test from the 'parameters' table, uses 170/160/48 and gets coordinates that disagree with every saved layout and with the gallery baselines. Someone reading D-024 believes the track still deviates from requirements and that restore does not exist. An anchor-compatibility change is made against 'math-2 is current'. The decision log, the document meant to answer 'what did we choose and when to revisit', is silent on the last nine days, which include the most data-sensitive features.

**Recommendation.** Add a one-line 'superseded by …' note to D-019, D-024.1, D-014/D-016, D-011 and the s5-network.md parameter table, or replace the numbers with a pointer to the constants. Add D-025 onwards for the post-S7 choices (cascade delete and backup policy, calibration model and its caps, background name preparation default-on and its threshold, restore semantics including the 'before-restore' backup, statistics counting rules and deleted-usage behaviour).

**Skeptic: partially confirmed, low.** Every factual claim I checked holds, but the failure scenarios are weaker than stated, so this is a low-severity documentation-drift finding rather than medium.

What holds:
- D-019 and the s5-network.md "参数" table still give 170 / 160 / 48 with no supersession note. The code uses 100 / 72 / 32, and the change is recorded only in s7-ui-polish.md:83.
- D-024 item 1 still says workspace deletion contradicts requirements §6.1 and that restore from backup is "not done". Requirements now cover deleting a whole workspace (requirements.md:347, 388), TRACK.md:86 marks the deviation resolved by v0.12, and TRACK.md:21/76 record in-app restore merged on 2026-11-12.
- D-016 ends at math-2 as the render profile; the code is math-3 (documented only in s7-ui-polish.md:111).
- D-011 has no pointer to the per-model calibration in s3-context.md:17.
- decisions.md ends at D-024; its last commit is 2026-11-04. A grep for "已取代 / superseded / 见 s7 / s7-ui" across decisions.md, s0–s6 docs and data-model.md returns nothing.

Why the severity is overstated:
1. decisions.md is a dated, append-only log. Each entry carries its date, and a dated decision that was later changed is normal for this format. The real defect is the missing forward pointers, not false statements.
2. The post-S7 choices are documented, just elsewhere: s3-context.md:17 (calibration, migration v9), s7-ui-polish.md (layout constants, math-3, deletion, name suggestions), stage-reports.md (dated sections through 2026-11-05 and later), and TRACK.md. "Silent on the last nine days" is true of decisions.md only, not of the documentatio … (cut; full text in the workflow journal)

*Corrections to the finding.* - Severity is low, not medium: this is drift in a dated log plus one stale parameter table, with no behavioural impact, and the current values are documented in s7-ui-polish.md, s3-context.md, stage-reports.md, TRACK.md and code comments.
- "The change is recorded only in s7-ui-polish.md:83" is overstated: NetworkLayout.cs:19–23 also records the old → new values in comments.
- "The decision log is silent on the last nine days" is true of decisions.md only; the same choices are recorded in stage-reports.md, s7-ui-polish.md, s3-context.md and TRACK.md.
- D-024's stale statement is partly mitigated by its own pointer to TRACK "待决定", where the deviation is marked resolved (TRACK.md:86).
- D-014 correctly says math-1 for its date; only D-016 ends at math-2. Citing D-014 as claiming math-2 is inaccurate.
- The layout-test scenario is unlikely in practice: a test author would reference the publ … (cut; full text in the workflow journal)

### docs-6. Design for backup restore, migration v10 and statistics is buried in a 248 KB 'UI polish' file; the design index omits half the directory and nothing links to run-guide.md

- Final: **low** (confirmed in part); auditor said medium
- Where: `docs/design/README.md:5`; effort M

**Evidence.** docs/design/README.md's table lists data-model, S0–S5, evolution-seams and decisions only. It does not list s6-wrapup.md (45 KB), s6-windows-checklist.md, s7-ui-polish.md (248 KB), s7-prototype-visual-language.md, stage-reports.md (85 KB) or run-guide.md (last edited 2026-10-28). A grep for 'run-guide' across TRACK.md, README.md, AGENTS.md, docs/*.md, docs/design/README.md and stage-reports.md returns nothing; only scripts/package-source.sh references it. In s7-ui-polish.md the last '## ' heading is line 364 '实现记录（多选与连带删除 · 2026-11-04）'; everything after it is '###' sub-sections under that heading, including '### 从备份恢复与备份清理（2026-11-12）' (line 644), '### 改名框里的灰字建议、后台准备名称与请求记录' (line 610, the only description of migration v10) and '### 使用统计页（2026-11-12）' (line 678). Token calibration (v9) is in s3-context.md; no document lists migrations v1–v10.

**Failure scenario.** A new contributor asked to change restore-from-backup (a whole-database replace) looks in the design index, finds S0's '恢复' paragraph, which describes only the post-migration-failure recovery page, and never finds the as-built design: pre-restore backup, exit-order prompts, no restart. That design sits at line 644 of a file named 'UI polish' under a 'multi-select delete' heading. Someone who wants to build and run the app finds no link to the run guide from any entry document.

**Recommendation.** Update docs/design/README.md to list every file with a one-line scope, and link run-guide.md from TRACK.md (接续点 'first command'). Split the post-S7 sections out of s7-ui-polish.md into purpose-named docs (for example backups-restore.md, statistics.md, workspace-names.md, notifications.md), or at least promote them to '##' headings and add a table of contents with a 'which doc describes which feature and migration' map.

**Skeptic: partially confirmed, low.** The structural facts hold, but the failure scenario is overstated because a second navigation path exists that the finding did not account for.

What holds:
- docs/design/README.md lists only data-model, S0-S5, evolution-seams and decisions. It omits s6-wrapup.md (45 KB), s6-windows-checklist.md, s7-ui-polish.md (248 KB), s7-prototype-visual-language.md, stage-reports.md (85 KB) and run-guide.md.
- run-guide.md is linked from no document. The only reference in the repository (excluding bin/obj/.git) is scripts/package-source.sh:36. TRACK.md:32 mentions a run guide ("运行说明") in prose with no link.
- In s7-ui-polish.md the last "## " heading is line 364 (multi-select and cascade delete, 2026-11-04). Workspace-name suggestions (578, 610), restore from backup (644) and the statistics page (678) are all "###" sub-sections under it.
- S0's recovery text (s0-skeleton-storage.md:55-56) covers only the startup recovery page, not the in-app restore.
- No design document enumerates migrations v1-v10; they are scattered across s1 (v2), evolution-seams (v3), s2 (v4), s3 (v5, v9), s4 (v6), s5 (v7), s6-wrapup (v8) and s7-ui-polish / stage-reports (v10).

What is overstated:
- "Never finds the as-built design" is not accurate. AGENTS.md sends contributors to TRACK.md first, and TRACK.md:26 links stage-reports.md. That file has purpose-named "##" headings per feature, e.g. line 37 "第 5 项：从备份恢复与备份清理" and line 5 "第 6 项：统计页", and each links on to s7-ui-polish.md naming the dated section (stage-reports.md:39). TRACK.md links s7-ui-polish.md four times, plus s6-wrapup.md and s6-windows-checklist. … (cut; full text in the workflow journal)

*Corrections to the finding.* - The claim that a contributor "never finds" the restore design is wrong: TRACK.md:26 -> stage-reports.md:37 (a "##" heading named for backup restore) -> s7-ui-polish.md is a working two-hop path.
- "Half the directory is unreachable" should read: the design README index is stale, but TRACK.md links stage-reports.md, s6-wrapup.md, s6-windows-checklist.md and s7-ui-polish.md. Only run-guide.md is orphaned; s7-prototype-visual-language.md was not checked for inbound links.
- s7-ui-polish.md:610 is not the only description of migration v10; stage-reports.md:78 and :87 also cover it.
- Severity should be low: this is discoverability and naming debt, not missing or wrong documentation.
- The recommendation still stands in reduced form: add the six missing rows to docs/design/README.md, link run-guide.md from TRACK.md or the root README, and promote the post-S7 sections to "##" or split them o … (cut; full text in the workflow journal)

### docs-7. run-guide.md is stale and incomplete as the track's only how-to-run document

- Final: **low** (not verified); auditor said low
- Where: `docs/design/run-guide.md:55`; effort S

**Evidence.** run-guide.md:55 '各阶段设计：`docs/design/s0-…` 到 `s6-…`' (S7 and all later work are missing). Line 31: '升级前会自动备份到数据目录下的 `backups/`，最多保留 5 份。升级失败时应用显示恢复页' — but SqliteBackupFiles.Prune rotates only BackupReason.Migration ('backups of any other reason are never pruned'), so '-before-delete', '-before-conversation-delete' and '-before-restore' backups accumulate without limit; in-app restore and backup deletion (设置 › 数据与存储) are not mentioned. Line 16 '全量在 Mac 上一两分钟', while the baseline run shows Desktop.Tests alone at 1 m 56 s. The opt-in switches are named loosely and incompletely: MINDWEAVE_CREDENTIAL_TESTS=1, MINDWEAVE_PERF, MINDWEAVE_SCREENSHOT_DIR / MINDWEAVE_GALLERY_THEME (gallery) and MINDWEAVE_REAL_API_REPORT appear in code or other docs but not here; scripts/windows.sh test|sync|exec, scripts/mailbox.sh and their env vars (MINDWEAVE_WIN_HOST/DIR/SOCKS/APP, MINDWEAVE_MAILBOX) are undocumented outside script headers and TRACK. The guide says 'dotnet test Mindweave.slnx -c Release' while TRACK.md:36 gives 'dotnet test Mindweave.slnx' (Debug). The root README it defers to says development has not started.

**Failure scenario.** A course grader or new contributor receives the source package (package-source.sh guarantees run-guide.md is in it), reads that backups are capped at five, and later finds the backups folder growing after deletions. They cannot find how to produce the screenshots the reports cite, or how to run credential tests, and are pointed to design docs that end at S6.

**Recommendation.** Refresh run-guide.md: an env-var table (all MINDWEAVE_* with defaults and purpose), the gallery command, the opt-in test switches, the backup kinds and which rotate, the in-app restore and delete location, realistic timings, one canonical test command, and an updated doc map including S7, stage reports and mailbox.

### docs-8. For the user to decide (main): requirements.md contradicts itself on deletion, and the shared docs' status lines say development has not started and cite v0.11

- Final: **low** (confirmed in part); auditor said medium
- Where: `docs/requirements.md:114`; effort S

**Evidence.** requirements.md:114 (§5.2): '首版同时包含总结版本管理、轻量 Markdown 导出、会话归档与无依赖删除' versus §8.2 (lines 232–238, v0.12): '删除保护已有成果的方式是“先说清范围再删”，而不是不让删…带子分支或总结的会话，删除时连同它的全部子分支…一起删除'. The v0.12 revision note lists '改动 §6.1、§8.2、§9.5' and §5.2 was not updated. Status lines: requirements.md:3 '正式开发尚未开始' and :14 '本轮仅整理文档，不启动开发' (version v0.13, updated 2026-11-12); architecture.md:4 '尚未启动正式开发，不是实施计划' (updated 2026-11-12); README.md:5 '当前状态 · 2026-10-25…（需求 v0.11）…没有待用户决定的产品事项…尚未启动正式开发'; evolution.md:7 and :156 '需求 v0.11'. Meanwhile this track has S0–S7 plus six post-S7 items merged. None of this is recorded in TRACK.md '待决定', which AGENTS.md requires for main-doc errors.

**Failure scenario.** The codex-steady track or a reviewer scoping v1 from §5.2 implements or grades 'dependency-free delete only' while §8.2 and §11 require cascade delete with stated scope. The two tracks could diverge on a destructive feature while both cite the same document. A reader of the README or the document headers concludes there is no product code and no pending decisions, and that v0.11 is current.

**Recommendation.** Record in TRACK.md '待决定' for the user to correct on main (do not edit on this branch): (1) align §5.2 line 114 with §8.2; (2) refresh the status lines of README.md, requirements.md and architecture.md, for example 'main holds no product code; development is in progress on the track branches, see each TRACK.md', and the 'v0.11' references in README.md and evolution.md.

**Skeptic: partially confirmed, low.** Every cited fact checks out, but the claimed failure scenario is weak, so this is a low-severity documentation-staleness item rather than medium.

What is true:
- requirements.md §5.2 (line 114) still says v1 includes "会话归档与无依赖删除", while §8.2 (lines 232-238) says deletion is "先说清范围再删" and cascades to sub-branches and summaries.
- The v0.12 revision note (line 450) lists §6.1, §8.2, §9.5 and the reliability acceptance as changed; §5.2 was missed.
- The stale status lines exist as quoted: requirements.md lines 3 and 14 (v0.13, updated 2026-11-12), architecture.md line 4, README.md line 5, evolution.md lines 7 and 156.
- These four files are identical to main on this branch (empty `git diff main HEAD`), so the fix does belong on main.
- TRACK.md "待决定" (lines 69-91) has no entry for either issue. Its only deletion entry is the resolved 2026-11-04 note that v0.12 corrected the rule on main.

Why the severity is overstated:
- Line 114 is a one-sentence scope summary that ends with "具体行为见后文", deferring to the later sections. A reader scoping v1 is sent to §8.2.
- Every normative location is consistent with cascade delete: §8.2, §6.1 line 130, §9.5 line 274, acceptance rows at lines 386-388, and architecture.md line 169. One leftover phrase against five consistent places makes a track implementing "dependency-free delete only" unlikely.
- The README status is explicitly date-stamped "当前状态 · 2026-10-25", and its next paragraph (line 7) says both tracks are developing from baseline-v0.11 and points to each TRACK.md. A reader is not really led to believe no development exists.
- "正式开发 … (cut; full text in the workflow journal)

*Corrections to the finding.* - Severity should be low, not medium. The §5.2 phrase is a leftover summary that defers to later sections, and all normative sections, the acceptance table and architecture.md agree on cascade delete, so the "tracks diverge on a destructive feature" scenario is improbable.
- The README status is an explicitly dated snapshot (2026-10-25) and the next paragraph points to the active tracks. The claim that a reader concludes there is no product code is overstated.
- evolution.md:156 is a dated decision record and is not wrong; only evolution.md:7 is stale.
- AGENTS.md says doc errors found during development are recorded in the track's TRACK.md for the user to decide. The absence of an entry is a missing note, not a rule violation with any product effect.

### docs-9. For the user to decide: shipped user-facing features have no requirement, acceptance scenario or consolidated list; each was added as '不改需求'

- Final: **low** (confirmed in part); auditor said medium
- Where: `TRACK.md:71`; effort M

**Evidence.** TRACK.md:72–77 marks items 1–6 as merged with '不改需求' (items 1–4), and items 5–6 have no requirements change either. Greps of docs/requirements.md and docs/architecture.md: '统计' 0 and 0 hits (statistics page with token usage, unit prices, cost estimates and milestones); '右键' 0 and 0; '深色/外观' 0 in requirements (light/dark theme, D-022); '新回答/提示音' 1 hit in requirements, 0 in architecture (toast, badge and sound); in-app restore of any backup and backup deletion (architecture §12.2 covers only the post-migration-failure recovery page and says '迁移备份不等于日常备份'); AI-suggested workspace names with background preparation, which TRACK.md:21 says is '默认开启' and issues AI requests the user did not start. requirements.md:293 still lists '手动拖动节点' under post-v1 while D-023 shipped it. Only the workspace-delete deviation ever went through the 'record in 待决定 → correct main' route.

**Failure scenario.** The shared requirements, which both tracks and the course submission treat as authoritative, no longer describe the product on this track. Reviewers cannot test statistics, restore or background name requests against any stated rule: for example, whether deleted conversations' usage should disappear, or whether a default-on background AI call is acceptable under §9.2/§9.4 cost transparency. These features will drift or regress with no spec to hold them. If the user later wants parity on the steady track, there is no single list of what was added beyond v0.13.

**Recommendation.** Add a short 'beyond-requirements feature register' to the track (feature, date, user decision reference, design section, data and cost implications, acceptance evidence) and list it in '待决定' so the user can choose per feature: promote to main requirements (with §11 scenarios), keep track-only, or remove. Call out the default-on background AI requests and whole-database restore specifically.

**Skeptic: partially confirmed, low.** The factual core holds: several shipped user-facing features on this track are not described in the shared requirements or architecture. My greps reproduce the auditor's counts: '统计' 0/0, '右键' 0/0, '深色' 0/0, '提示音' 0/0, and '新回答' has 1 hit in requirements (line 147, about retry attempts, unrelated to notifications). requirements.md:293 still lists '手动拖动节点' as post-v1 while decisions.md D-023 delivered it early. TRACK.md:72-75 does tag items 1-4 '不改需求'.

The finding is overstated in four ways, which is why I lower it to low.

1. These are not silent drift. Every listed feature was requested or designed by the user and accepted on a recorded date. TRACK.md:71 says the user accepted all eight items on 2026-11-10 and that only items 7 and 8 need a `main` requirements update first. Requirements v0.13 §10.3 was then written on `main` and merged (commits 1086f87, 1ba95c9). The split between 'needs requirements' and '不改需求' therefore reflects a decision already put to the user. It also matches requirements.md:421 ('新增范围、费用行为或明显改变使用方式时再讨论'): they were discussed. AGENTS.md only requires recording in TRACK.md and letting the user decide on `main`; that route was followed.

2. 'No consolidated list' is wrong. TRACK.md:71-79 is a numbered list of the post-v1 additions with merge dates. docs/design/stage-reports.md has one section per feature (lines 5, 37, 67, 97, 127, 160, 192, 222), each with how to try it, what was built, a verification table and what is unverified. decisions.md D-022/D-023/D-024 cover the theme, node dragging and workspace deletion. What is actually missing is narrower … (cut; full text in the workflow journal)

*Corrections to the finding.* - 'No consolidated list' is wrong: TRACK.md:71-79 lists the additions with dates, and stage-reports.md has a section per feature with verification evidence. The real gap is that there is no per-feature promote/keep/remove status and no decisions.md entry after D-024.
- 'Each was added as 不改需求' applies to items 1-4 only, and that classification was part of a list the user accepted on 2026-11-10. Items 5-6 simply carry no note.
- The deleted-usage question is already an open user decision at TRACK.md:80, not an unnoticed gap.
- Default-on background name requests were specified by the user and ship with a settings switch, idle priority, no run at startup, and request recording; they are not an undisclosed cost behaviour. Whether the settings text states the cost was not checked.
- Restore is partially covered by requirements.md:266 (user-chosen, never auto-overwrite). Only backup deletion … (cut; full text in the workflow journal)

### docs-10. Open defects and operational knowledge have no register: unfixed issues live only as tail sentences of dated findings, and the Windows tester's instructions are not in the repo

- Final: **low** (not verified); auditor said low; already known in TRACK.md
- Where: `TRACK.md:155`; effort S

**Evidence.** TRACK.md:155 (finding dated 2026-10-31) ends: '产品里的竞争仍在：读者在页面底部、长回答仍在排版时按下鼠标，页面可能跳到末尾。留待之后处理。' stage-reports.md:492 repeats '对应一个真实的滚动竞态…未修'. No later doc records a fix, and ConversationView.axaml.cs:40–55 still follows the bottom on IsAtBottom() with no pointer-down guard. TRACK.md:23 buries '偶发：整方案并行全量时 `TransactionKillTests` 删除中断测试失败一次，复跑 13 次未再现' inside the test-count bullet rather than in 发现. TRACK.md:84 (small-context models rejected during compression) sits in '待决定' mixed with resolved items ('（已解决 …）' entries at lines 83, 86, 87). TRACK.md:103 has an orphaned instruction paragraph ('及时记下值得稳健轨提前知道的问题…') in the middle of the findings list, and the findings are not in date order (11-12, 11-10, 11-06, 11-05, then 10-25 … 10-28, 10-27, 11-02). docs/mailbox.md:42–44 tells Windows Claude to act 'under your working instructions' and use 'the format in your instructions', but those instructions (test by SHA in E:\mindweave-wintest, never push, report format) exist only as one sentence in TRACK.md:94 and outside the repo.

**Failure scenario.** A known user-visible scroll jump and a flaky kill-during-delete test (a data-integrity test) are forgotten because nobody rereads a 2026-10-31 paragraph. If the Windows machine's Claude configuration is lost or a new tester is set up, its boundaries and report format cannot be reconstructed from the repository, and mailbox.md's safety rule 'those boundaries still apply' points at nothing versioned.

**Recommendation.** Add an '未解决问题' (open issues) section to TRACK.md with one line each (symptom, first seen, status, pointer); move the follow-bottom race, the TransactionKillTests flake and the small-window compression limit there; delete resolved '待决定' entries or move them to a 'resolved' list. Commit the Windows tester's working instructions (for example docs/windows-tester.md) and link them from mailbox.md.

