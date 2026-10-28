# S4 版本选择器原生发现修复审查 · 2026-10-28

原生源`6374f6c`发现同版本保存／恢复后选择框变空。固定修复`77f71cc`→`790b000`，真实编译控件3项行为RED→GREEN，加脏编辑元数据刷新回归共4项；最终聚焦77、完整673通过／2原有跳过、build零警告错误。独立软件限定审查Approved，0C／0I／0M；仍待重建Mac新包后原生复测。

# Native picker N1 — scoped spec and quality review

## Spec compliance

- ✅ Compliant for the bounded software fix. **N1 addressed in source `790b000c6c5e33c2a019b7f44f81aafde7110723`** against `77f71ccc105a0ad39f53ca0593d2193bcc9d205d`. New related findings: **0 Critical / 0 Important / 0 Minor**.
- `SummaryViewModel.cs:96,121,124–135` reconciles the existing collection rather than clearing it: retained immutable header objects remain, deleted IDs are removed, and additions are inserted at their descending ordinal positions. Starting from the already sorted projection, removal preserves order and indexed insertion preserves that order for one or multiple additions. No preferred index, binding reset, timer, or extra store/body/read operation is introduced.
- `SummaryVersionDesktopTests.Picker.cs:15–51` exercises no-change Save, Restore of the viewed historical version, and Adopt of the viewed candidate. Each checks the actual ComboBox selection alongside VM/body/current identity, reading state, version count, clean draft and exactly one explicit version-load notification.
- `SummaryVersionDesktopTests.Picker.cs:53–63` sends real headless keyboard Up/Down events to that control, waits for pending operations, and checks historical selection/body with the adopted/restored/saved current pointer unchanged.
- `SummaryVersionDesktopTests.Picker.cs:67–109` checks retained control object identity with an open dropdown during candidate/metadata refresh and node rename, actual TextBox input retention, no extra load/read change, new Manual3 selection, descending `3/2/1` order, candidate retention, and exact picker-object retention on another no-change Save.
- ⚠️ Rebuilt packaged native acceptance remains the controller's gate. These compiled-control/SQLite regressions and source review do not establish the result in the running native application; that application was not touched.

## Strengths

- The shared private reconciliation helper removes the original reset cause while reusing the existing metadata-refresh policy (`SummaryViewModel.cs:124–135`). The change stays within the two authorized source/test files and leaves bindings and mutation protocols intact.
- The tests inspect the real compiled `SummaryView` and its actual `SummaryVersionPicker`, rather than only asserting the VM property. The unchanged `Surface` constructs that view with the tested DataContext and a Window (`SummaryVersionDesktopTests.cs:93–114`); the new tests include both operation regressions and user keyboard navigation.

## Named caller risks checked

- **Spurious selection reentry / stale body:** inspected only the unchanged load entry (`SummaryViewModel.cs:64–85`), the actual one-way binding (`SummaryView.axaml:20`), and handler (`SummaryView.axaml.cs:106–111`). Full loads set `IsLoading` before reconciliation; null selection, loading, and already-displayed IDs are ignored by the handler. Metadata insertion retains the selected object. Together with the notification assertions above, no new reentry/body race was found.
- **Deleted-header fallback / draft/current regression:** inspected only unchanged Save dispatch (`SummaryViewModel.cs:145–159`), operation projection routing (`SummaryViewModel.Operations.cs:246–270`), and existing deletion/control tests (`SummaryVersionDesktopTests.cs:43–76`). Removed viewed bodies are cleared and routed through a full current-version load; unrelated deletion/metadata paths retain the displayed item. Full-load reconciliation removes deleted headers while its loading guard suppresses incidental control events. Save still selects precisely its commit result; the helper changes no draft, read epoch or pointer writes.

## Issues

- Critical: none.
- Important: none.
- Minor: none.

## Evidence and assessment

- Read the fixed 12,445-byte / two-file review package **once**, plus the brief/report and the narrowly identified unchanged caller/control helpers above. Did not regenerate the diff, crawl S4, rerun tests/build, use a compiler, or inspect/interact with native processes, profiles, vaults or data.
- Inspected existing raw evidence under `artifacts/s4-native-picker-fix/`: `red-behavior-compiled.log:13–67` records three actual null-SelectedItem behavioral failures at Picker.cs line 51; `green-picker-final.log:18–20` records 4/4; `summary-desktop-final.log:91–93` records 77/77; `full-release-final.log:693–696` records 673 passed / 2 existing CredentialVault skips / 0 failures; `build-release-final.log:7–11` records 0 warnings / 0 errors. The two skip identities are at full log lines 409–417. Earlier scaffolding compile failures are explicitly separated from behavioral RED in the implementer report.
- Branch/worktree checks identify the requested `track/codex-steady` checkout at `790b000`; the controller's existing TRACK and verification-document edits were preserved. Sole review output is this report.
- **Task quality: Approved.** N1's concrete collection-reset cause is addressed with a small identity-preserving change and behavior regressions that cover same-version operations and related metadata/editor paths. No specific unanswered software risk warranted another test run. Native acceptance remains pending independently.
