# 低／信息级发现分拣（2026-11-14）

依据：[健康检查](README.md) 的 171 条低／信息级发现；你 2026-11-13 决定“先分拣，快速项限一天内修，其余进待办”。四个只读审查分组逐条对照当前代码（track/claude-auto-healthfix 3c93626）分类；结论按条列出，修复进展在“状态”列之后由提交补记。

合计 171 条：快速修复 94，需设计或决定 49，不值得做 20，已解决 8，不成立 0。

快速修复按影响先后处理：数据、崩溃、安全 → 界面 → 开发工具。一天内没做完的留在本表，状态为“待做”。

## 快速修复（限时一天，按影响排序处理）

| 编号 | 问题 | 处理 | 影响 |
|---|---|---|---|
| storage-3 | Restore swap not crash-safe; empty DB created | If mindweave.db missing but restore leftovers exist, report IoError instead of creating a new one. | data |
| storage-4 | Restored copy not flushed | Flush the copy to disk before the swap. | data |
| storage-6 | Failing migration retries add a backup each | Prune upgrade backups also when migration fails. | data |
| ai-client-6 | Transport error after finish_reason fails complete answer | Forgive IOException/HttpRequestException after completion. | data |
| ui-vm-10 | Composer clears text typed during send | Clear only if Text still equals the sent text. | data |
| architecture-3 | In-app restore swap has no failure path | try/catch around EndAsync; continue to restore. | data |
| cross-platform-2 | macOS SQLite commits not fullfsync'd | PRAGMA fullfsync=ON; measure cost. | data |
| tests-6 | No exhaustive enum mapping tests | Theories over Enum.GetValues for SqlNames/UiText. | data |
| concurrency-6 | async void OnAnswerFinished crashes on throw | Guarded handler. | crash |
| errors-7 | async-void and worker-thread events unisolated | Same as concurrency-6; loop flag in finally. | crash |
| errors-8 | Listing backups can throw | TryParse; per-file catch. | crash |
| observability-9 | FileLoggerProvider catches only IOException | Catch access/not-supported; fall back. | crash |
| security-4 | Unbounded response bodies and SSE lines | Bounded error read; SSE line cap. | crash |
| maintainability-5 | async void comment wrong; worker-thread throw kills process | Wrap ConversationChanged invoke in try/catch+log. | crash |
| architecture-1 | Storage page sync unguarded file I/O | TryParse and per-file try/catch in backup listing. | crash |
| cross-platform-10 | Logging catches only IOException | Add UnauthorizedAccess/Security. | crash |
| observability-10 | Error text truncated before key scrub | Scrub then truncate. | security |
| gap-unprompted-8 | Name requests include archived conversations | Filter hidden conversations out of input. | security |
| security-3 | Error detail cut before key scrub | Scrub then truncate. | security |
| security-5 | Base URL userinfo accepted and stored | Reject non-empty UserInfo. | security |
| security-6 | Deleted content recoverable from DB file | PRAGMA secure_delete=ON. | security |
| security-7 | Link suppression untested | Headless test: link click does not launch. | security |
| ci-build-8 | Workflow hardening (permissions, stress.yml injection) | permissions: contents read; inputs via env; SHA pins. | security |
| dependencies-5 | No dependabot; actions on mutable tags | dependabot.yml; SHA pins. | security |
| repo-scripts-6 | CI permissions/tags | Overlaps ci-build-8. | security |
| repo-scripts-10 | real-api.sh unvalidated profile | Validate PROFILE; anchor localhost regex. | security |
| gap-native-interop-5 | Open data folder uses bare explorer.exe/open | Absolute paths; catch Win32Exception. | security |
| gap-native-interop-6 | Pipe client lacks CurrentUserOnly | Add it; backoff in listener. | security |
| concurrency-3 | Enqueue after commit unguarded vs concurrent exit | Catch closed-coordinator enqueue; finish as cancelled; reject. | UX |
| concurrency-10 | Single-instance listener can stall | Read timeout; catch-all with back-off. | UX |
| gap-lifecycle-3 | Exit waits unbounded; queued cancelled first | Cancel running first. | UX |
| gap-lifecycle-6 | Deleting viewed conversation wipes Explore history | Keep remaining stack. | UX |
| gap-lifecycle-7 | Close before DB open nests shutdown | Return Stage != Releasing after Begin. | UX |
| ai-client-7 | Planner swaps small tails for larger assumed summaries | Skip chunk when summary cost >= raw cost. | UX |
| ai-client-9 | Tiny context windows accepted, every question refused | Cap unset reserve at capacity/4 or raise minimum. | UX |
| ai-client-10 | Error classification coarse | Tighten context-limit phrases; map in-stream error codes. | UX |
| gap-unprompted-9 | Generated titles keep control/bidi characters | Drop Cc/Cf chars in shared clean step. | UX |
| gap-unprompted-10 | Statistics use cached local time zone | TimeZoneInfo.ClearCachedData() before load. | UX |
| requirements-7 | One-summary-task rule checked before write only | In-transaction check / lock. | UX |
| requirements-8 | Failed naming enqueue leaves group marked queued | Remove key on failure. | UX |
| gap-summary-3 | Version ops don't invalidate open edit session | Bump Generation after operation. | UX |
| gap-summary-4 | Version-history ticks wiped by reload | Keep IsSelected for surviving ids. | UX |
| gap-summary-5 | Exported Markdown title/source unescaped | Escape Markdown punctuation in heading/source. | UX |
| gap-summary-6 | Second-step failure reports nothing stored | Name check best-effort after commit. | UX |
| gap-summary-10 | Titles cut by UTF-16 index split surrogates | Cut by text elements. | UX |
| gap-branch-anchor-3 | Cannot-locate fallback shows 30 chars, never clears | Fuller quote; dismiss. | UX |
| gap-branch-anchor-4 | Large selections refused despite small quote | Relaxed JSON encoder; omit nulls. | UX |
| gap-branch-anchor-6 | Branch action no feedback; navigates after user moved | Navigate only if page still shown. | UX |
| gap-branch-anchor-7 | Return-to-source highlights but does not select | Correct tooltip/comments. | UX |
| gap-branch-anchor-8 | Question rows build hidden AnswerViews | Split templates. | UX |
| gap-branch-anchor-9 | Branch creation reads all parent messages | Read source message by id. | UX |
| ui-vm-5 | Settings links land on stale sub-page; duplicate home | NavigationRequest has no content at 3 sites. Add "pop to Settings home" navigation; dedupe singleton routes in PushCore. Test: stack shape after link. | UX |
| ui-vm-6 | NetworkCanvas keeps panning after capture lost | OnPointerCaptureLost clears only _pressed, not _panFrom. Clear _panFrom there. Test: headless capture loss mid-pan. | UX |
| ui-vm-8 | Node moved during LoadAsync is reverted | `_positions = reference` at end of LoadAsync. Compare _revision and merge. Test: Move between awaits. | UX |
| ui-vm-9 | Statistics reload discards open price editor | Models.Clear()+Add rebuilds rows. Reconcile by ModelKey. | UX |
| performance-4 | List query returns full first-question text | substr(b.text,1,N) when title null. | UX |
| performance-8 | Live answers never released from memory | TryRemove after finish. | UX |
| maintainability-6 | ArchiveStorageFailed reused; partial archive says nothing changed | Split text; report archived count. | UX |
| maintainability-7 | GenerationCoordinator._live never pruned | Same as performance-8. | UX |
| cross-platform-3 | Relaunch during shutdown acked then exits | Don't ack once releasing. | UX |
| cross-platform-8 | Trailing separator in data-dir override | TrimEndingDirectorySeparator. | UX |
| cross-platform-9 | Only answer text pins a Chinese font | Apply CJK font list app-wide. | UX |
| dependencies-6 | RenderProfile hard-codes LiveMarkdown version | Test profile matches assembly version. | UX |
| gap-native-interop-7 | Disk probe misclassifies read-only/quota on Unix | Map EROFS/EDQUOT per OS. | UX |
| concurrency-8 | Debounce timers fire after dispose | Disposed flag in continuations. | dev |
| observability-5 | No session banner in the log | One start line with version, channel, OS, schema. | dev |
| observability-6 | Probe failure logs full exception every 5 s | Once per pause, then counts. | dev |
| observability-8 | Connection tests leave no trace | Log outcome and status. | dev |
| gap-branch-anchor-2 | Anchor round-trip untested beyond formulas | Corpus test (table/code/list/CRLF/emoji). | dev |
| maintainability-8 | Dead members and test-only surface | Delete dead members. | dev |
| ci-build-4 | No .editorconfig; dotnet format unchecked | .editorconfig, reformat, CI format step. | dev |
| ci-build-5 | Credential stores never run in CI | Set MINDWEAVE_CREDENTIAL_TESTS=1 on Windows leg. | dev |
| ci-build-6 | CI only on track branch; no trx/artifacts | trx logger, blame-hang, upload results on failure. | dev |
| ci-build-10 | Publish/package scripts: stale output, no SHA | Clear output, BUILD_INFO, .env pattern. | dev |
| dependencies-7 | Prototypes can't restore under central packages | prototypes/Directory.Build.props opt-out. | dev |
| docs-2 | TRACK.md status lines unreadable | Move history to changelog. | dev |
| docs-3 | Windows checklist row 25 outdated | Rewrite to v0.12 behaviour. | dev |
| docs-4 | data-model.md deletion rules outdated | As-built rules; migration table. | dev |
| docs-5 | decisions.md superseded values; stops at D-024 | Superseded notes; new entries. | dev |
| docs-6 | Design index omits half the files | Update docs/design/README.md. | dev |
| docs-7 | run-guide.md stale | Update docs map and backup rotation. | dev |
| docs-10 | No register of open defects | 未解决问题 section in TRACK.md. | dev |
| repo-scripts-3 | Prototypes don't restore | Duplicate of dependencies-7. | dev |
| repo-scripts-5 | package-source.sh output location, leftovers | artifacts/, temp-then-rename, .env pattern. | dev |
| repo-scripts-8 | mailbox.sh wait hides unreachable | Capture remote status. | dev |
| tests-3 | FakeChatClient stream shape unrealistic | ResponseStarted first, Usage after Completed. | dev |
| tests-4 | Platform credential stores never run in tests | Same as ci-build-5. | dev |
| tests-9 | Gallery scenes share temp dirs across worktrees | Per-run GUID parent. | dev |
| concurrency-4 | Terminal write depends on subscribers not throwing | Guard event invocations. | none |
| gap-lifecycle-5 | Workspace delete skips per-conversation notifications | Notify deleted ids. | none |
| gap-unprompted-4 | Title request sends whole first question | Truncate question to a few hundred chars. | none |
| gap-unprompted-5 | Failed name preparation retried on every title | Record failed attempt / session backoff. | none |
| architecture-7 | Per-session logger/HttpClient never disposed | Register via factory lambdas. | none |
| repo-scripts-7 | windows.sh sync ignores errexit | ` |  |

## 需要设计、迁移或你的决定（待办）

| 编号 | 问题 | 处理 | 影响 |
|---|---|---|---|
| storage-7 | Cascade-delete backups never rotated | Retention policy; owner chose manual cleanup page. | data |
| gap-schema-3 | No-DDL format changes lack a version gate | Data-format version policy. | data |
| gap-schema-5 | Restore from TooNew page strands newer data | Documented trade-off. | data |
| gap-schema-6 | Restoring old backup duplicates upgrade backups | Backup policy. | data |
| gap-schema-7 | One dangling row blocks all upgrades | FK-violation policy. | data |
| dependencies-4 | Single-maintainer native renderer on untrusted output | Fuzzing / safe mode. | crash |
| security-2 | No remove-key action; orphaned OS-store keys | New feature + store enumeration. | security |
| dependencies-2 | No lock file / source mapping | Restore policy. | security |
| dependencies-3 | Self-contained build bundles publisher's runtime patch | Release pipeline deferred. | security |
| repo-scripts-9 | Personal paths/email committed | History rewrite is owner's decision before going public. | security |
| errors-9 | Exit-save failure offers only retry/return | "Quit anyway" changes architecture §12.3. | UX |
| observability-7 | No diagnostics surface | D2 deferral. | UX |
| gap-lifecycle-4 | SummaryDrafts not told of deleted node | Cross-service delete signal. | UX |
| gap-schema-4 | Upgrade invisible | Phase reporting UX. | UX |
| ai-client-8 | Summaries sent as mid-conversation system messages | Documented in s3-context.md; message-form choice. | UX |
| gap-unprompted-6 | Stopped/failed requests omitted from cost totals | Deliberate (s7); UX decision. | UX |
| gap-unprompted-7 | Single current price re-prices all history | Effective-dated prices need schema. | UX |
| requirements-4 | Summaries share priority with user questions | Conflicts with §9.2 text; owner/doc on main. | UX |
| requirements-6 | Planner may summarize latest exchange first | Planner policy. | UX |
| gap-summary-7 | Network load writes per node; stale places after discard | Store method + dirty-layout policy. | UX |
| gap-summary-9 | Same-name disambiguation never re-evaluated | Re-check policy. | UX |
| gap-branch-anchor-5 | Table/list/code quotes lose structure | Quote format product choice. | UX |
| a11y-i18n-5 | Light-theme text colours fail WCAG AA | Palette change needs owner approval vs prototypes. | UX |
| a11y-i18n-6 | Every conversation row is a Tab stop | Keyboard model for sidebar. | UX |
| a11y-i18n-7 | Multi-select rows distinguished by tint only | Owner-approved look 2026-11-05. | UX |
| a11y-i18n-8 | No reduced-motion / high-contrast preference | Per-OS query + settings toggle. | UX |
| a11y-i18n-9 | Thin screen-reader semantics in conversation | Announcement design. | UX |
| performance-3 | Sidebar builds all rows eagerly | Virtualization changes template/selection/keyboard. | UX |
| performance-5 | Page reload double-reads; hidden pages reload | Lazy reload policy. | UX |
| performance-7 | Cascading delete copies whole DB; backups never pruned | D-005 decision; retention is owner's call. | UX |
| performance-9 | Checkpoints rewrite whole answer each second | Storage format change. | UX |
| cross-platform-4 | macOS no .app bundle/icon/signature | Packaging deferred. | UX |
| cross-platform-7 | Windows second launch can't take foreground | Needs Windows test first. | UX |
| gap-native-interop-1 | Keychain read blocks, no timeout | Security behaviour change. | UX |
| storage-8 | data-model.md out of step with schema | Doc on main; owner approves. | dev |
| architecture-4 | Optional ctor params blind ValidateOnBuild | Many test constructors. | dev |
| architecture-5 | Restore/exit orchestration in Desktop VMs | Layering refactor. | dev |
| architecture-8 | Per-caller interfaces grew to 19 | Pending owner decision in TRACK. | dev |
| cross-platform-5 | Native credential stores lack coverage | Opt-in by design. | dev |
| ci-build-7 | Builds not reproducible (SDK, lock files) | Dependency policy. | dev |
| ci-build-9 | Gallery and perf budgets never run in CI | CI minutes choice. | dev |
| repo-scripts-2 | Codex review files untracked in stale worktree | Other agent's files; owner decides. | dev |
| repo-scripts-4 | Stale worktree-wf branches/worktrees | Deleting touches other sessions; owner go-ahead. | dev |
| tests-7 | No coverage, no per-test timeout | New dependency/CI policy. | dev |
| tests-10 | Application has no direct tests | Project structure decision. | dev |
| storage-9 | Cross-row invariants only in app code | Composite FKs need a migration. | none |
| gap-unprompted-3 | Same-name naming re-sends whole group, uncapped | Documented (§10.2); capping is owner's call. | none |
| docs-8 | requirements §5.2 vs §8.2 on main | Shared docs on main; owner decides. | none |
| docs-9 | Shipped features lack requirements register | Owner decides per feature. | none |

## 不值得做（附理由）

| 编号 | 问题 | 处理 | 影响 |
|---|---|---|---|
| gap-native-interop-9 | Data dir default permissions | Under private ~/Library. | security |
| tests-8 | UI tests run with motion off | Speculative. | UX |
| gap-native-interop-3 | Store failures all shown as locked | Message already covers it. | UX |
| requirements-9 | Real credential stores tested only opt-in | Needs desktop session. | dev |
| performance-6 | Perf harness opt-in, no write benchmarks | Documented design (E6). | dev |
| maintainability-9 | Near-total public surface | Info. | dev |
| architecture-2 | ViewModelBoundaryTests narrow | Bounded by reviewed proposal. | dev |
| architecture-6 | LayeringTests substring scan holes | Hypothetical. | dev |
| architecture-9 | PageFactory/SqliteDatabase concentration | Info. | dev |
| tests-5 | UI tests wait fixed 300 ms | Can only false-pass. | dev |
| concurrency-7 | StorageHealth.Changed inside owners' locks | All handlers traced safe. | none |
| concurrency-9 | _live and CTS never pruned | KBs per answer; risk to Observe. | none |
| gap-lifecycle-8 | Killed request leaves no ai_request row | By design. | none |
| gap-schema-8 | Migration v3 uses live Redaction code | Installed data is v8+. | none |
| gap-summary-8 | Export writer edge cases | Rare; wrong message only. | none |
| a11y-i18n-10 | Mixed culture number formatting | Chinese-only UI by design. | none |
| performance-10 | message_block WITHOUT ROWID overflow | Table-rebuild migration for ~15%. | none |
| dependencies-8 | RaTeX lacks win-arm64/x86 natives | x64 is the scope. | none |
| gap-native-interop-4 | Windows 2560-byte blob limit | Keys far below. | none |
| gap-native-interop-8 | Plain-text key copies cleared partly | No real protection. | none |

## 已由此前的修复解决

| 编号 | 问题 | 处理 | 影响 |
|---|---|---|---|
| ui-vm-7 | Pages lack load-failure/loading state | 2b66936 names and logs failed reads. | UX |
| cross-platform-6 | Start-up failures invisible | 62feebc. | UX |
| gap-native-interop-2 | Settings claims key saved | 6a27d71. | UX |
| storage-5 | Lock/permission open errors reported as corruption | 94e82e8. | none |
| concurrency-5 | Page reloads fire-and-forget | 2b66936. | none |
| requirements-5 | Older page instance shows stale draft | 012513c. | none |
| maintainability-4 | Task-run skeletons drifted | 9b5d759. | none |
| gap-native-interop-10 | Dev/release share data dir | bbcfa1a channel split. | none |
