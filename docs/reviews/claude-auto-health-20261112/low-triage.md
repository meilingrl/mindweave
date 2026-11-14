# 低／信息级发现分拣（2026-11-14）

依据：[健康检查](README.md) 的 171 条低／信息级发现；你 2026-11-13 决定“先分拣，快速项限一天内修，其余进待办”。四个只读审查分组逐条对照当前代码（track/claude-auto-healthfix 3c93626）分类；结论按条列出，修复进展在“状态”列之后由提交补记。

合计 171 条：快速修复 94，需设计或决定 49，不值得做 20，已解决 8，不成立 0。

快速修复按影响先后处理：数据、崩溃、安全 → 界面 → 开发工具。一天的限时内（2026-11-14）修了 30 条（数据、崩溃、安全类全部处理完，另有 5 条界面类；其中 repo-scripts-6 只做了权限部分），每条先写测试；5 条改判（见状态列）。其余快速项状态为“待做”，留作待办。

修完后另做了一次独立审查，确认的 10 处问题已在同一分支改正并补了测试（编码与 BOM、模型列表限长、请求号限长、Network 页删除后留在原区、只有 `.restoring` 才阻止新建数据库、失败升级保留刚做的备份、保留 ZWNJ、管道权限异常、日志写入器释放、关机时入队的竞态、Linux 打开器回退）。

## 快速修复（限时一天，按影响排序处理）

| 编号 | 问题 | 处理 | 影响 | 状态 |
|---|---|---|---|---|
| storage-3 | Restore swap not crash-safe; empty DB created | If mindweave.db missing but restore leftovers exist, report IoError instead of creating a new one. | data | 已修 d4856cb |
| storage-4 | Restored copy not flushed | Flush the copy to disk before the swap. | data | 已修 d4856cb |
| storage-6 | Failing migration retries add a backup each | Prune upgrade backups also when migration fails. | data | 已修 d4856cb |
| ai-client-6 | Transport error after finish_reason fails complete answer | Forgive IOException/HttpRequestException after completion. | data | 已修 2a3ae6e |
| ui-vm-10 | Composer clears text typed during send | Clear only if Text still equals the sent text. | data | 已修 2a3ae6e |
| architecture-3 | In-app restore swap has no failure path | try/catch around EndAsync; continue to restore. | data | 已修 2a3ae6e |
| cross-platform-2 | macOS SQLite commits not fullfsync'd | PRAGMA fullfsync=ON; measure cost. | data | 已定 2026-11-14：只开 checkpoint_fullfsync（把日志并入数据库时完全落盘，断电不会损坏文件；本机每次并入约 2→9 ms），普通提交不改（断电最多丢最后几秒）。 |
| tests-6 | No exhaustive enum mapping tests | Theories over Enum.GetValues for SqlNames/UiText. | data | 已修 afcacf2 |
| concurrency-6 | async void OnAnswerFinished crashes on throw | Guarded handler. | crash | 已修 e14cbf8 |
| errors-7 | async-void and worker-thread events unisolated | Same as concurrency-6; loop flag in finally. | crash | 已修 e14cbf8 |
| errors-8 | Listing backups can throw | TryParse; per-file catch. | crash | 已修 8473cf3 |
| observability-9 | FileLoggerProvider catches only IOException | Catch access/not-supported; fall back. | crash | 已修 8473cf3 |
| security-4 | Unbounded response bodies and SSE lines | Bounded error read; SSE line cap. | crash | 部分修复 40a9106：错误正文、整段回答、模型列表都限长；一个事件的多行累计限 8M 字符。没有换行的单行仍在 StreamReader 内部无上限读入，需要换掉按行读取，留作待办。 |
| maintainability-5 | async void comment wrong; worker-thread throw kills process | Wrap ConversationChanged invoke in try/catch+log. | crash | 已修 e14cbf8 |
| architecture-1 | Storage page sync unguarded file I/O | TryParse and per-file try/catch in backup listing. | crash | 已修 8473cf3 |
| cross-platform-10 | Logging catches only IOException | Add UnauthorizedAccess/Security. | crash | 已修 8473cf3 |
| observability-10 | Error text truncated before key scrub | Scrub then truncate. | security | 已修 40a9106 |
| gap-unprompted-8 | Name requests include archived conversations | Filter hidden conversations out of input. | security | 已定 2026-11-14：保持发送。只发标题，且这些会话的全文当初已发给服务；归档的意思是“不在侧栏”，不是“保密”。 |
| security-3 | Error detail cut before key scrub | Scrub then truncate. | security | 已修 40a9106 |
| security-5 | Base URL userinfo accepted and stored | Reject non-empty UserInfo. | security | 已修 40a9106 |
| security-6 | Deleted content recoverable from DB file | PRAGMA secure_delete=ON. | security | 已定 2026-11-14：不开。删除前本来就做备份，备份里仍有全文，开了也做不到“删了就没了”；真要彻底清除应做成单独功能（同时清库文件与对应备份）。 |
| security-7 | Link suppression untested | Headless test: link click does not launch. | security | 改为“不值得做”：要在无界面渲染里点中行内链接的确切位置，测试脆弱；防护只有一行，模型文字不加载任何内容已有图片测试覆盖。 |
| ci-build-8 | Workflow hardening (permissions, stress.yml injection) | permissions: contents read; inputs via env; SHA pins. | security | 已修 35696e3 |
| dependencies-5 | No dependabot; actions on mutable tags | dependabot.yml; SHA pins. | security | 已定 2026-11-14：不用 Dependabot（它只读默认分支 main，那里没有代码）；CI 每次推送检查有安全公告的包（直接与间接依赖），有则失败；两个 CI 动作固定到提交。 |
| repo-scripts-6 | CI permissions/tags | Overlaps ci-build-8. | security | 已修 35696e3（SHA 固定未做） |
| repo-scripts-10 | real-api.sh unvalidated profile | Validate PROFILE; anchor localhost regex. | security | 已修 7f6f33c |
| gap-native-interop-5 | Open data folder uses bare explorer.exe/open | Absolute paths; catch Win32Exception. | security | 已修 1215184 |
| gap-native-interop-6 | Pipe client lacks CurrentUserOnly | Add it; backoff in listener. | security | 已修 5a2fa26 |
| concurrency-3 | Enqueue after commit unguarded vs concurrent exit | Catch closed-coordinator enqueue; finish as cancelled; reject. | UX | 已修 0edb2d0 |
| concurrency-10 | Single-instance listener can stall | Read timeout; catch-all with back-off. | UX | 已修 5a2fa26 |
| gap-lifecycle-3 | Exit waits unbounded; queued cancelled first | Cancel running first. | UX | 待做 |
| gap-lifecycle-6 | Deleting viewed conversation wipes Explore history | Keep remaining stack. | UX | 已修 512d6c7 |
| gap-lifecycle-7 | Close before DB open nests shutdown | Return Stage != Releasing after Begin. | UX | 待做 |
| ai-client-7 | Planner swaps small tails for larger assumed summaries | Skip chunk when summary cost >= raw cost. | UX | 待做 |
| ai-client-9 | Tiny context windows accepted, every question refused | Cap unset reserve at capacity/4 or raise minimum. | UX | 待做 |
| ai-client-10 | Error classification coarse | Tighten context-limit phrases; map in-stream error codes. | UX | 待做 |
| gap-unprompted-9 | Generated titles keep control/bidi characters | Drop Cc/Cf chars in shared clean step. | UX | 已修 196bb5f |
| gap-unprompted-10 | Statistics use cached local time zone | TimeZoneInfo.ClearCachedData() before load. | UX | 待做 |
| requirements-7 | One-summary-task rule checked before write only | In-transaction check / lock. | UX | 待做 |
| requirements-8 | Failed naming enqueue leaves group marked queued | Remove key on failure. | UX | 待做 |
| gap-summary-3 | Version ops don't invalidate open edit session | Bump Generation after operation. | UX | 待做 |
| gap-summary-4 | Version-history ticks wiped by reload | Keep IsSelected for surviving ids. | UX | 待做 |
| gap-summary-5 | Exported Markdown title/source unescaped | Escape Markdown punctuation in heading/source. | UX | 待做 |
| gap-summary-6 | Second-step failure reports nothing stored | Name check best-effort after commit. | UX | 待做 |
| gap-summary-10 | Titles cut by UTF-16 index split surrogates | Cut by text elements. | UX | 已修 196bb5f |
| gap-branch-anchor-3 | Cannot-locate fallback shows 30 chars, never clears | Fuller quote; dismiss. | UX | 待做 |
| gap-branch-anchor-4 | Large selections refused despite small quote | Relaxed JSON encoder; omit nulls. | UX | 待做 |
| gap-branch-anchor-6 | Branch action no feedback; navigates after user moved | Navigate only if page still shown. | UX | 待做 |
| gap-branch-anchor-7 | Return-to-source highlights but does not select | Correct tooltip/comments. | UX | 待做 |
| gap-branch-anchor-8 | Question rows build hidden AnswerViews | Split templates. | UX | 待做 |
| gap-branch-anchor-9 | Branch creation reads all parent messages | Read source message by id. | UX | 待做 |
| ui-vm-5 | Settings links land on stale sub-page; duplicate home | NavigationRequest has no content at 3 sites. Add "pop to Settings home" navigation; dedupe singleton routes in PushCore. Test: stack shape after link. | UX | 待做 |
| ui-vm-6 | NetworkCanvas keeps panning after capture lost | OnPointerCaptureLost clears only _pressed, not _panFrom. Clear _panFrom there. Test: headless capture loss mid-pan. | UX | 已修 512d6c7 |
| ui-vm-8 | Node moved during LoadAsync is reverted | `_positions = reference` at end of LoadAsync. Compare _revision and merge. Test: Move between awaits. | UX | 待做 |
| ui-vm-9 | Statistics reload discards open price editor | Models.Clear()+Add rebuilds rows. Reconcile by ModelKey. | UX | 待做 |
| performance-4 | List query returns full first-question text | substr(b.text,1,N) when title null. | UX | 待做 |
| performance-8 | Live answers never released from memory | TryRemove after finish. | UX | 待做 |
| maintainability-6 | ArchiveStorageFailed reused; partial archive says nothing changed | Split text; report archived count. | UX | 待做 |
| maintainability-7 | GenerationCoordinator._live never pruned | Same as performance-8. | UX | 待做 |
| cross-platform-3 | Relaunch during shutdown acked then exits | Don't ack once releasing. | UX | 待做 |
| cross-platform-8 | Trailing separator in data-dir override | TrimEndingDirectorySeparator. | UX | 待做 |
| cross-platform-9 | Only answer text pins a Chinese font | Apply CJK font list app-wide. | UX | 待做 |
| dependencies-6 | RenderProfile hard-codes LiveMarkdown version | Test profile matches assembly version. | UX | 待做 |
| gap-native-interop-7 | Disk probe misclassifies read-only/quota on Unix | Map EROFS/EDQUOT per OS. | UX | 待做 |
| concurrency-8 | Debounce timers fire after dispose | Disposed flag in continuations. | dev | 待做 |
| observability-5 | No session banner in the log | One start line with version, channel, OS, schema. | dev | 待做 |
| observability-6 | Probe failure logs full exception every 5 s | Once per pause, then counts. | dev | 待做 |
| observability-8 | Connection tests leave no trace | Log outcome and status. | dev | 待做 |
| gap-branch-anchor-2 | Anchor round-trip untested beyond formulas | Corpus test (table/code/list/CRLF/emoji). | dev | 待做 |
| maintainability-8 | Dead members and test-only surface | Delete dead members. | dev | 待做 |
| ci-build-4 | No .editorconfig; dotnet format unchecked | .editorconfig, reformat, CI format step. | dev | 待做 |
| ci-build-5 | Credential stores never run in CI | Set MINDWEAVE_CREDENTIAL_TESTS=1 on Windows leg. | dev | 待做 |
| ci-build-6 | CI only on track branch; no trx/artifacts | trx logger, blame-hang, upload results on failure. | dev | 待做 |
| ci-build-10 | Publish/package scripts: stale output, no SHA | Clear output, BUILD_INFO, .env pattern. | dev | 待做 |
| dependencies-7 | Prototypes can't restore under central packages | prototypes/Directory.Build.props opt-out. | dev | 待做 |
| docs-2 | TRACK.md status lines unreadable | Move history to changelog. | dev | 待做 |
| docs-3 | Windows checklist row 25 outdated | Rewrite to v0.12 behaviour. | dev | 待做 |
| docs-4 | data-model.md deletion rules outdated | As-built rules; migration table. | dev | 待做 |
| docs-5 | decisions.md superseded values; stops at D-024 | Superseded notes; new entries. | dev | 待做 |
| docs-6 | Design index omits half the files | Update docs/design/README.md. | dev | 待做 |
| docs-7 | run-guide.md stale | Update docs map and backup rotation. | dev | 待做 |
| docs-10 | No register of open defects | 未解决问题 section in TRACK.md. | dev | 待做 |
| repo-scripts-3 | Prototypes don't restore | Duplicate of dependencies-7. | dev | 待做 |
| repo-scripts-5 | package-source.sh output location, leftovers | artifacts/, temp-then-rename, .env pattern. | dev | 待做 |
| repo-scripts-8 | mailbox.sh wait hides unreachable | Capture remote status. | dev | 待做 |
| tests-3 | FakeChatClient stream shape unrealistic | ResponseStarted first, Usage after Completed. | dev | 待做 |
| tests-4 | Platform credential stores never run in tests | Same as ci-build-5. | dev | 待做 |
| tests-9 | Gallery scenes share temp dirs across worktrees | Per-run GUID parent. | dev | 待做 |
| concurrency-4 | Terminal write depends on subscribers not throwing | Guard event invocations. | none | 待做 |
| gap-lifecycle-5 | Workspace delete skips per-conversation notifications | Notify deleted ids. | none | 待做 |
| gap-unprompted-4 | Title request sends whole first question | Truncate question to a few hundred chars. | none | 已修 196bb5f |
| gap-unprompted-5 | Failed name preparation retried on every title | Record failed attempt / session backoff. | none | 待做 |
| architecture-7 | Per-session logger/HttpClient never disposed | Register via factory lambdas. | none | 待做 |
| repo-scripts-7 | windows.sh sync ignores errexit | ` | 待做 |

## 需要设计、迁移或你的决定（待办）

| 编号 | 问题 | 处理 | 影响 | 状态 |
|---|---|---|---|---|
| storage-7 | Cascade-delete backups never rotated | Retention policy; owner chose manual cleanup page. | data | 待决定 |
| gap-schema-3 | No-DDL format changes lack a version gate | Data-format version policy. | data | 待决定 |
| gap-schema-5 | Restore from TooNew page strands newer data | Documented trade-off. | data | 待决定 |
| gap-schema-6 | Restoring old backup duplicates upgrade backups | Backup policy. | data | 待决定 |
| gap-schema-7 | One dangling row blocks all upgrades | FK-violation policy. | data | 待决定 |
| dependencies-4 | Single-maintainer native renderer on untrusted output | Fuzzing / safe mode. | crash | 待决定 |
| security-2 | No remove-key action; orphaned OS-store keys | New feature + store enumeration. | security | 待决定 |
| dependencies-2 | No lock file / source mapping | Restore policy. | security | 待决定 |
| dependencies-3 | Self-contained build bundles publisher's runtime patch | Release pipeline deferred. | security | 待决定 |
| repo-scripts-9 | Personal paths/email committed | History rewrite is owner's decision before going public. | security | 待决定 |
| errors-9 | Exit-save failure offers only retry/return | "Quit anyway" changes architecture §12.3. | UX | 待决定 |
| observability-7 | No diagnostics surface | D2 deferral. | UX | 待决定 |
| gap-lifecycle-4 | SummaryDrafts not told of deleted node | Cross-service delete signal. | UX | 待决定 |
| gap-schema-4 | Upgrade invisible | Phase reporting UX. | UX | 待决定 |
| ai-client-8 | Summaries sent as mid-conversation system messages | Documented in s3-context.md; message-form choice. | UX | 待决定 |
| gap-unprompted-6 | Stopped/failed requests omitted from cost totals | Deliberate (s7); UX decision. | UX | 待决定 |
| gap-unprompted-7 | Single current price re-prices all history | Effective-dated prices need schema. | UX | 待决定 |
| requirements-4 | Summaries share priority with user questions | Conflicts with §9.2 text; owner/doc on main. | UX | 待决定 |
| requirements-6 | Planner may summarize latest exchange first | Planner policy. | UX | 待决定 |
| gap-summary-7 | Network load writes per node; stale places after discard | Store method + dirty-layout policy. | UX | 待决定 |
| gap-summary-9 | Same-name disambiguation never re-evaluated | Re-check policy. | UX | 待决定 |
| gap-branch-anchor-5 | Table/list/code quotes lose structure | Quote format product choice. | UX | 待决定 |
| a11y-i18n-5 | Light-theme text colours fail WCAG AA | Palette change needs owner approval vs prototypes. | UX | 待决定 |
| a11y-i18n-6 | Every conversation row is a Tab stop | Keyboard model for sidebar. | UX | 待决定 |
| a11y-i18n-7 | Multi-select rows distinguished by tint only | Owner-approved look 2026-11-05. | UX | 待决定 |
| a11y-i18n-8 | No reduced-motion / high-contrast preference | Per-OS query + settings toggle. | UX | 待决定 |
| a11y-i18n-9 | Thin screen-reader semantics in conversation | Announcement design. | UX | 待决定 |
| performance-3 | Sidebar builds all rows eagerly | Virtualization changes template/selection/keyboard. | UX | 待决定 |
| performance-5 | Page reload double-reads; hidden pages reload | Lazy reload policy. | UX | 待决定 |
| performance-7 | Cascading delete copies whole DB; backups never pruned | D-005 decision; retention is owner's call. | UX | 待决定 |
| performance-9 | Checkpoints rewrite whole answer each second | Storage format change. | UX | 待决定 |
| cross-platform-4 | macOS no .app bundle/icon/signature | Packaging deferred. | UX | 待决定 |
| cross-platform-7 | Windows second launch can't take foreground | Needs Windows test first. | UX | 待决定 |
| gap-native-interop-1 | Keychain read blocks, no timeout | Security behaviour change. | UX | 待决定 |
| storage-8 | data-model.md out of step with schema | Doc on main; owner approves. | dev | 待决定 |
| architecture-4 | Optional ctor params blind ValidateOnBuild | Many test constructors. | dev | 待决定 |
| architecture-5 | Restore/exit orchestration in Desktop VMs | Layering refactor. | dev | 待决定 |
| architecture-8 | Per-caller interfaces grew to 19 | Pending owner decision in TRACK. | dev | 待决定 |
| cross-platform-5 | Native credential stores lack coverage | Opt-in by design. | dev | 待决定 |
| ci-build-7 | Builds not reproducible (SDK, lock files) | Dependency policy. | dev | 待决定 |
| ci-build-9 | Gallery and perf budgets never run in CI | CI minutes choice. | dev | 待决定 |
| repo-scripts-2 | Codex review files untracked in stale worktree | Other agent's files; owner decides. | dev | 待决定 |
| repo-scripts-4 | Stale worktree-wf branches/worktrees | Deleting touches other sessions; owner go-ahead. | dev | 待决定 |
| tests-7 | No coverage, no per-test timeout | New dependency/CI policy. | dev | 待决定 |
| tests-10 | Application has no direct tests | Project structure decision. | dev | 待决定 |
| storage-9 | Cross-row invariants only in app code | Composite FKs need a migration. | none | 待决定 |
| gap-unprompted-3 | Same-name naming re-sends whole group, uncapped | Documented (§10.2); capping is owner's call. | none | 待决定 |
| docs-8 | requirements §5.2 vs §8.2 on main | Shared docs on main; owner decides. | none | 待决定 |
| docs-9 | Shipped features lack requirements register | Owner decides per feature. | none | 待决定 |

## 不值得做（附理由）

| 编号 | 问题 | 处理 | 影响 | 状态 |
|---|---|---|---|---|
| gap-native-interop-9 | Data dir default permissions | Under private ~/Library. | security | 不做 |
| tests-8 | UI tests run with motion off | Speculative. | UX | 不做 |
| gap-native-interop-3 | Store failures all shown as locked | Message already covers it. | UX | 不做 |
| requirements-9 | Real credential stores tested only opt-in | Needs desktop session. | dev | 不做 |
| performance-6 | Perf harness opt-in, no write benchmarks | Documented design (E6). | dev | 不做 |
| maintainability-9 | Near-total public surface | Info. | dev | 不做 |
| architecture-2 | ViewModelBoundaryTests narrow | Bounded by reviewed proposal. | dev | 不做 |
| architecture-6 | LayeringTests substring scan holes | Hypothetical. | dev | 不做 |
| architecture-9 | PageFactory/SqliteDatabase concentration | Info. | dev | 不做 |
| tests-5 | UI tests wait fixed 300 ms | Can only false-pass. | dev | 不做 |
| concurrency-7 | StorageHealth.Changed inside owners' locks | All handlers traced safe. | none | 不做 |
| concurrency-9 | _live and CTS never pruned | KBs per answer; risk to Observe. | none | 不做 |
| gap-lifecycle-8 | Killed request leaves no ai_request row | By design. | none | 不做 |
| gap-schema-8 | Migration v3 uses live Redaction code | Installed data is v8+. | none | 不做 |
| gap-summary-8 | Export writer edge cases | Rare; wrong message only. | none | 不做 |
| a11y-i18n-10 | Mixed culture number formatting | Chinese-only UI by design. | none | 不做 |
| performance-10 | message_block WITHOUT ROWID overflow | Table-rebuild migration for ~15%. | none | 不做 |
| dependencies-8 | RaTeX lacks win-arm64/x86 natives | x64 is the scope. | none | 不做 |
| gap-native-interop-4 | Windows 2560-byte blob limit | Keys far below. | none | 不做 |
| gap-native-interop-8 | Plain-text key copies cleared partly | No real protection. | none | 不做 |

## 已由此前的修复解决

| 编号 | 问题 | 处理 | 影响 | 状态 |
|---|---|---|---|---|
| ui-vm-7 | Pages lack load-failure/loading state | 2b66936 names and logs failed reads. | UX | 已解决 |
| cross-platform-6 | Start-up failures invisible | 62feebc. | UX | 已解决 |
| gap-native-interop-2 | Settings claims key saved | 6a27d71. | UX | 已解决 |
| storage-5 | Lock/permission open errors reported as corruption | 94e82e8. | none | 已解决 |
| concurrency-5 | Page reloads fire-and-forget | 2b66936. | none | 已解决 |
| requirements-5 | Older page instance shows stale draft | 012513c. | none | 已解决 |
| maintainability-4 | Task-run skeletons drifted | 9b5d759. | none | 已解决 |
| gap-native-interop-10 | Dev/release share data dir | bbcfa1a channel split. | none | 已解决 |
