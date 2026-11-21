# Interface language: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every piece of text in Mindweave exists in English and Chinese, and the user can switch between them in place, without a restart. English is the default.

**Architecture:** One process-wide `UiLanguage` in Application. Short text is written once, side by side, as `T("English", "中文")`. Long prompts live in one embedded file per language. A switch disposes and rebuilds the view layer from a view provider built on a lasting root provider, then replays where the user was.

**Tech Stack:** .NET 10, Avalonia 11 (headless tests), xUnit, Microsoft.Extensions.DependencyInjection, SQLite.

**Spec:** [bilingual-ui.md](bilingual-ui.md); requirements §10.5 and architecture §3.5 (v0.24).

## Global Constraints

- Two languages only: `UiLanguage.English`, `UiLanguage.Chinese`. The default is English (from merge 2 on; Chinese in merge 1, so merge 1 changes nothing the user sees).
- The language choice is stored in the file `language` in the channel's data folder, containing `en` or `zh-Hans`. Missing, unreadable or unknown means English. It is never stored in the database.
- English wording follows the architecture §2.3 glossary (summary, summary network, branch, branch source, anchor, citation, digest, workspace, conversation, new exploration, …). Chinese wording stays exactly as it is today.
- Language names are never translated: "English", "简体中文".
- `UiText` has no `const` strings and no cached `{ get; } =` statics. Member names stay the same.
- Core holds no language state.
- Stored data is never converted. Stored names (tables, columns, preference keys) do not change.
- Every merge: merge the latest `main` in, run `scripts/test.sh full` (with `MINDWEAVE_DATA_DIR` set to the worktree), merge, push, and check that CI is green on Linux and Windows. STATUS.md changes only on `main`. Tell Main-Agent the merge SHA.
- The spec commit (3c9c761) merges only after the user confirms it. It travels with merge 1.

## Review Focus

1. **Switching twice in quick succession** (two clicks, or keyboard arrows on the picker): exactly one rebuild runs at a time, and the window ends in the last language chosen. Test in Task 12.
2. **A message held by a service across a switch**, such as an MCP server error shown in Settings: after the switch it shows in the new language, not the old one. Test in Task 5.
3. **Long English labels** in fixed-width places (sidebar rows, toolbar buttons, settings rows, dialogs) clip or overflow. Covered by the screenshot review in Task 13; any clipping found is fixed there.
4. **The language file can't be read or written** (permission denied, a folder in its place, garbage content): the app starts in English without crashing, and a failed write still switches for this run, with a notice. Test in Task 7.
5. **Switching while a summary is in edit mode with unsaved text, or while a summary is being generated:** edit mode and its text come back, and generation continues. Test in Task 11.

---

## Merge 1: the text mechanism and the English text

### Task 1: The language and `T`

**Files:**
- Create: `src/Mindweave.Application/Languages/UiLanguage.cs`, `CurrentLanguage.cs`, `Bilingual.cs`, `BilingualText.cs`
- Test: `tests/Mindweave.Infrastructure.Tests/Languages/BilingualTests.cs`

**Interfaces:**
- Produces:
  - `enum UiLanguage { English, Chinese }`
  - `static class CurrentLanguage { UiLanguage Value { get; }; void Set(UiLanguage language); }`, which starts as `Chinese` in merge 1
  - `static class Bilingual { string T(string english, string chinese); string Plural(int n, string one, string many); CultureInfo Culture { get; } }`
    - `Plural` returns `"1 conversation"` or `"3 conversations"`; it covers the English side only.
    - `Culture` is `en-US` or `zh-CN`.
  - `sealed record BilingualText(string English, string Chinese)`, whose `ToString()` returns `T(English, Chinese)`.

- [ ] **Step 1: Write failing tests** in a test class marked `[Collection("Language")]`, with that collection defined using `DisableParallelization = true`:
  - `T_returns_the_current_language`: after `Set(English)`, `T("Cancel","取消") == "Cancel"`; after `Set(Chinese)`, it returns `"取消"`.
  - `Plural_picks_singular_for_one`: `Plural(1,"conversation","conversations") == "1 conversation"`, and `Plural(3, …) == "3 conversations"`.
  - `BilingualText_follows_a_later_switch`: create one `BilingualText`, switch the language, and `ToString()` follows.
  - `Culture_follows_the_language`: `Culture.Name` is `"en-US"` or `"zh-CN"`.
  - Each test restores `Chinese` in `finally`.
- [ ] **Step 2:** Run `dotnet test tests/Mindweave.Infrastructure.Tests --filter FullyQualifiedName~BilingualTests`. Expected: build fails (the types don't exist).
- [ ] **Step 3:** Implement. `CurrentLanguage` is a `volatile` static field. Add the glossary-named folder `Languages`.
- [ ] **Step 4:** Run the tests again. Expected: PASS.
- [ ] **Step 5:** Commit: `Languages: UiLanguage, T, BilingualText (bilingual UI merge 1)`.

### Task 2: The completeness test

**Files:**
- Test: `tests/Mindweave.Desktop.Tests/Text/UiTextCompletenessTests.cs`

**Interfaces:**
- Consumes: `CurrentLanguage`, from Task 1.

- [ ] **Step 1: Write the tests.** Use `[Collection("Language")]`, defined in this assembly with `DisableParallelization = true`.
  - `Every_member_is_English_under_English`: set English, and reflect over every public static property and method of `UiText` that returns `string`.
    - Call methods with sample arguments: `string` → `"x"`, `int` → `2`, `long` → `2`, `double` → `2.5`, `TimeSpan` → `90 s`, `DateOnly` → `2026-11-20`, `DateTimeOffset` → `2026-11-20T21:38+08:00`, `IEnumerable<string>` → `["a","b"]`, an enum → its first value, `bool` → both values.
    - Any other parameter type fails the test, naming the member, so the sample table gets extended.
    - Assert that no result matches `\p{IsCJKUnifiedIdeographs}|[　-〿＀-￯]`. The only allowed exception is the member that returns "简体中文" (from Task 12).
    - The failure lists every offending member.
  - `Every_member_is_non_empty_under_Chinese`: same reflection; every result is non-empty.
  - Restore `Chinese` in `finally`.
- [ ] **Step 2:** Run `dotnet test tests/Mindweave.Desktop.Tests --filter FullyQualifiedName~UiTextCompletenessTests`. Expected: the English test FAILS and lists about 830 members.
- [ ] **Step 3:** Commit the test as failing (`UiText completeness test (fails until the translation)`). Task 3 makes it pass.

### Task 3: Convert and translate `UiText`

**Files:**
- Modify: all 21 files `src/Mindweave.Desktop/Text/UiText*.cs`, plus any call site the build rejects (a `const` context such as an attribute argument or a default parameter value).

**Interfaces:**
- Consumes: `Bilingual.T`, `Bilingual.Plural`, `Bilingual.Culture` through `using static Mindweave.Application.Languages.Bilingual;`
- Produces: the same member names. Every member is `=> T("…", "…")` or a method that builds both strings.

- [ ] **Step 1: Translate in batches with a workflow.** One translator per group of `UiText` files, then one independent reviewer per group. Rules for the translator:
  - Turn `const string X = "中文";` into `static string X => T("English", "中文");` and cached statics into `=>`.
  - Methods build both strings, with `Plural` and `Culture` on the English side.
  - Keep the Chinese byte for byte.
  - English: glossary terms, sentence case, no full stop on labels and buttons, a full stop on full sentences, the menu path separator ` › ` as today, typographic quotes “ ” and ’.
  
  The reviewer compares meaning, glossary terms, placeholders and plurals, and fixes in place.
- [ ] **Step 2:** Merge in the strings from `task/session-network` if it has merged by then: `UiText.SummaryNetwork.cs` (7 strings), `DayLabel(DateOnly, DateOnly)` → `"Nov 20"` / `"Nov 20, 2025"`, `DotHoverLabel(string, DateTimeOffset)` → `"Nov 20 21:38"`.
- [ ] **Step 3:** Run `dotnet build Mindweave.slnx` (or the solution file). Expected: no errors. Fix the `const`-context call sites.
- [ ] **Step 4:** Run the Task 2 tests. Expected: PASS.
- [ ] **Step 5:** Run `scripts/test.sh full`. Expected: all pass (the app is still in Chinese).
- [ ] **Step 6:** Commit per group (`UiText: both languages for <files>`).

### Task 4: Text outside `UiText`, plus a guard

**Files:**
- Modify:
  - `src/Mindweave.Desktop/ViewModels/Conversations/ComposerAttachments.cs:175` (`"图片.png"`)
  - `ViewModels/Conversations/AttachmentChip.cs:163` (`$"附件：{Name}"`)
  - `ViewModels/WorkspaceToastViewModel.cs:59` (`"。"`)
  - `ViewModels/Pages/McpServersPanel.cs:97` (`"、"`)
  - `Views/Conversations/ComposerView.axaml:108` (`AutomationProperties.Name="提问"`)
  - Desktop uses of `CitationBlock.Label` (`AttachmentViewer`, `MessageItemViewModel`) and of `ContentBlocks.PastedTextName`
- Modify: `src/Mindweave.Desktop/Text/UiText.Attachments.cs` and `UiText.Conversation.cs` for the new members
- Test: `tests/Mindweave.Architecture.Tests/ChineseTextTests.cs`

**Interfaces:**
- Produces:
  - `UiText.PastedImageName` → `"image.png"` / `"图片.png"`
  - `UiText.AttachmentAccessibleName(string name)`
  - `UiText.Sentences(string first, string second)`, which joins with `". "` or `"。"`
  - `UiText.List(IEnumerable<string>)`, which joins with `", "` or `"、"`
  - `UiText.AskAccessibleName`
  - `UiText.CitationLabel(CitationKind kind, string title)`
  - `UiText.PastedTextName`

- [ ] **Step 1: Write the failing test** `Desktop_has_no_Chinese_outside_UiText`. Scan `src/Mindweave.Desktop/**/*.cs` outside `Text/`, and `**/*.axaml` with XML comments removed. Fail on any line with a Chinese character or full-width punctuation that isn't inside a `//` or `///` comment, and list them.
- [ ] **Step 2:** Run `dotnet test tests/Mindweave.Architecture.Tests`. Expected: FAIL, listing the 5 sites above.
- [ ] **Step 3:** Replace each site with the `UiText` member. New pasted attachments take their name from `UiText.PastedTextName` / `PastedImageName` when created. Core's `PastedTextName` and `Label` stay only for model-facing use until Task 15.
- [ ] **Step 4:** Run the test again. Expected: PASS. Then run the Desktop tests that cover attachments and citations (`--filter "FullyQualifiedName~Attachment|FullyQualifiedName~Citation"`). Expected: PASS.
- [ ] **Step 5:** Commit: `Desktop text outside UiText moves in; guard test`.

### Task 5: Messages the services hold for the user

**Files:**
- Modify: `src/Mindweave.Application/Tools/Mcp/McpServerSettings.cs:64-138, 240, 327`, `McpServerManager.cs:115-429`, `src/Mindweave.Application/Storage/StorageAdmissionRefusedException.cs`, `src/Mindweave.Application/Conversations/TaskRunners.cs:138, 239`, and the Desktop places that show them
- Test: `tests/Mindweave.Infrastructure.Tests/Languages/ServiceTextTests.cs`

**Interfaces:**
- Consumes: `BilingualText`, from Task 1.
- Produces:
  - Validation problems become `BilingualText?`.
  - An MCP server entry's error and a tool's disabled reason become `BilingualText?`.
  - `McpServerException` and `StorageAdmissionRefusedException` carry a `BilingualText Text`, with `Message => Text.ToString()`.
  - Text written into the database stays a `string`: `Text.ToString()` at write time.
- Rule: a message that outlives the call that made it is a `BilingualText`. A string shown at once may use `T`.

- [ ] **Step 1: Write failing tests** in `[Collection("Language")]`:
  - `Validation_problem_follows_a_switch`: validate an MCP server with an empty name under Chinese; the problem is `"请填写服务器的名称。"`. Switch to English and it reads `"Enter the server's name."`.
  - `Halted_server_error_follows_a_switch`: drive the fake MCP server until the manager halts it; the entry's error renders in each language after a switch.
- [ ] **Step 2:** Run the tests. Expected: build fails or the tests FAIL.
- [ ] **Step 3:** Implement the types above. Translate each message, with the same review as Task 3. Desktop binds `.ToString()` when it builds the view, so a rebuild re-reads it.
- [ ] **Step 4:** Run the tests and the MCP test classes (`--filter FullyQualifiedName~Mcp` in both test projects). Expected: PASS.
- [ ] **Step 5:** Commit: `Service messages held for the user are BilingualText`.

### Task 6: Merge 1

- [ ] Ask the user to confirm the spec commit 3c9c761, if they haven't already.
- [ ] Merge the latest `main` in and convert any new `UiText` strings. Run `scripts/test.sh full`. Merge with `--no-ff`. On `main`, commit STATUS ("bilingual UI merge 1: text in both languages; still Chinese"). Push. Check CI on both systems. Tell Main-Agent and the session-network session.

---

## Merge 2: the switch

### Task 7: The stored choice and the pinned tests

**Files:**
- Create:
  - `src/Mindweave.Application/Languages/ILanguageChoice.cs` (`Task<UiLanguage> ReadAsync()`, `Task<bool> WriteAsync(UiLanguage)`; reads also have a synchronous `UiLanguage Read()` for startup)
  - `src/Mindweave.Infrastructure/Languages/FileLanguageChoice.cs`, the file `language` in the channel data folder
  - `tests/*/PinChinese.cs`: a `[ModuleInitializer]` in each test project that touches text: Core, Infrastructure, Desktop, Architecture
- Modify:
  - `CurrentLanguage`: default `English`
  - `src/Mindweave.Desktop/AppHost.cs` or `Program.cs`: `CurrentLanguage.Set(choice.Read())` before the first window
- Test: `tests/Mindweave.Infrastructure.Tests/Languages/FileLanguageChoiceTests.cs`

**Interfaces:**
- Produces:
  - `ILanguageChoice`
  - `FileLanguageChoice(string dataFolder)`
  - File content `en` or `zh-Hans`, UTF-8, no newline required

- [ ] **Step 1: Write failing tests:**
  - `Missing_file_means_English`
  - `Garbage_means_English`: content `"fr"`, empty, or binary
  - `Folder_in_its_place_means_English_and_write_returns_false`
  - `Round_trip`: write Chinese, then read Chinese
  - `Write_is_atomic`: write through a temporary file and rename
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement. Register it in the root provider. Add the `PinChinese` initializers so existing tests keep their Chinese assertions.
- [ ] **Step 4:** Run `scripts/test.sh full`. Expected: PASS.
- [ ] **Step 5:** Commit: `Language choice in a file; English by default; tests pinned to Chinese`.

### Task 8: Root provider and view provider

**Files:**
- Modify: `src/Mindweave.Desktop/CompositionRoot.cs`, `AppHost.cs` (`AppSession`), `App.axaml.cs:53-67`
- Test: `tests/Mindweave.Desktop.Tests/ViewProviderTests.cs`

**Interfaces:**
- Produces:
  - `CompositionRoot.BuildRoot(...) : ServiceProvider`. It holds Infrastructure, Application, the platform services (`IThemeApplier`, `IUiDispatcher`, `ISystemShell`, `SystemAlertSound`, `IInAppRestore`), the four request bridges, `SummaryNetworkSessions`, `ILanguageChoice` and, after session-network merges, `SessionNetworkGate`.
  - `CompositionRoot.BuildView(IServiceProvider root) : ServiceProvider`. It holds every other Desktop registration (view models, dialogs, `ExitCoordinator`, `AppStartup`). Each root service it needs is registered with `AddSingleton(instance)`, never with a factory.
  - `AppSession.Root` (root provider), `AppSession.View` (current view provider), `event EventHandler? ViewReplaced`.
  - The existing exception guard, shutdown and instance listening resolve through `AppSession` each time, never through a captured provider.

- [ ] **Step 1: Write failing tests:**
  - `Two_views_share_root_services`: two `BuildView` calls from one root give the same `GenerationCoordinator` and different `ShellViewModel`s.
  - `Disposing_a_view_keeps_root_services`: after disposing a view provider, a root `IDisposable` service is not disposed and still works.
  - `Backup_restore_still_replaces_everything`: the existing restore test still passes.
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Split the registrations. The backup restore builds a new root and a new view, as today.
- [ ] **Step 4:** Run the new tests and `AppHostLifecycleTests` and `BackupRestoreUiTests`. Expected: PASS.
- [ ] **Step 5:** Commit: `Composition: root provider and view provider`.

### Task 9: View-layer singletons unsubscribe

**Files:**
- Modify: `ConversationListViewModel.cs:53,74-78`, `WorkspaceNewsViewModel.cs:89-96`, `ConversationDeletionViewModel.cs:75-76`, `WorkspaceDeletionViewModel.cs:58`, `BackupDialogs.cs:91,241`, `ShellViewModel.cs:46-57`, `Services/AppStartup.cs:74`
- Test: `tests/Mindweave.Desktop.Tests/ViewRebuildTests.cs`

**Interfaces:**
- Produces: each class implements `IDisposable` with named handlers, so disposing the view provider unsubscribes them.

- [ ] **Step 1: Write failing tests:**
  - `Rebuild_leaves_service_handler_counts_unchanged`: count the subscribers of each service event (through a test hook or reflection on the backing delegate) before and after `BuildView`, dispose the old view, then `BuildView` again. The counts are equal.
  - `One_toast_and_one_sound_after_a_rebuild`: rebuild, then finish an answer in another workspace. Exactly one toast is shown and one alert sound is played.
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement `Dispose` for each class.
- [ ] **Step 4:** Run the tests. Expected: PASS.
- [ ] **Step 5:** Commit: `View-layer singletons unsubscribe when disposed`.

### Task 10: `RootView`

**Files:**
- Create: `src/Mindweave.Desktop/Views/RootView.axaml(.cs)`, holding everything `MainWindow.axaml` holds today except the window itself (overlays, dialogs, 27 strings)
- Modify: `Views/MainWindow.axaml(.cs)`. Its content becomes a `RootView`, replaced on `ViewReplaced`. `Follow` moves its dialog-focus handling into `RootView`.
- Test: the existing `DialogKeyboardTests`, `ExitPromptTests` and `GalleryTests` still pass. A new `RootView_is_replaced_and_window_is_kept` test checks the same `MainWindow` instance, a new `RootView`, and the same window size.

- [ ] **Step 1:** Write `RootView_is_replaced_and_window_is_kept`. Run it. Expected: FAIL.
- [ ] **Step 2:** Move the content.
- [ ] **Step 3:** Run the new test plus `DialogKeyboardTests`, `ExitPromptTests` and `GalleryTests`. Expected: PASS.
- [ ] **Step 4:** Commit: `RootView holds the window's content`.

### Task 11: Snapshot, replay and the switch itself

**Files:**
- Create: `src/Mindweave.Desktop/Navigation/ViewSnapshot.cs`, `src/Mindweave.Desktop/Services/LanguageSwitch.cs`
- Modify:
  - `Navigation/Workbench.cs`: `ViewSnapshot Snapshot()`, `void Restore(ViewSnapshot)`
  - `ViewModels/Pages/SummaryViewModel.cs`: edit mode is snapshotted
  - The settings forms that carry input over: `ModelSettingsViewModel`, `McpServersPanel`, `WebSearchSettingsPanel`
  - The elapsed-time display in `ConversationViewModel.cs:571-584`: computed from the run's start time
- Test: `tests/Mindweave.Desktop.Tests/LanguageSwitchTests.cs`, in `[Collection("Language")]`

**Interfaces:**
- Produces:
  - `sealed record ViewSnapshot(WorkspaceId? Active, IReadOnlyDictionary<WorkspaceId, WorkspaceRoutes> Routes, string? SettingsSubpage, double SettingsScroll, SummaryEditState? Edit, SettingsFormInput? Forms)`. `WorkspaceRoutes` holds the section and its route stack, with `ConversationRoute.SourceOf` removed.
  - `LanguageSwitch.SwitchAsync(UiLanguage target) : Task`. It runs these steps in order:
    1. Wait until no dialog, layout prompt, exit stage, backup flow or attachment store is in progress.
    2. Take the snapshot.
    3. Detach the old `RootView`, so pages save their reading position and drafts.
    4. `CurrentLanguage.Set`.
    5. Build the view.
    6. Restore the snapshot.
    7. Show the new `RootView`.
    8. Dispose the old view.
    9. Write the choice.
    
    Calls made while a switch is running are queued, and only the last target is applied. It never calls `AppStartup.RunAsync`.

- [ ] **Step 1: Write failing tests:**
  - `Switch_keeps_a_streaming_answer`: start an answer with a fake model gated mid-stream, switch, then release the gate. The new page shows the full answer, and the message ends Completed, not Interrupted.
  - `Switch_keeps_a_pending_approval`: an MCP side-effect call is waiting. After a switch the card is there, and approving it runs the call.
  - `Switch_keeps_draft_reading_position_and_settings_subpage`
  - `Switch_keeps_summary_edit_mode_and_unsaved_text` (Review Focus 5)
  - `Switch_keeps_summary_generation_running` (Review Focus 5)
  - `Switch_keeps_network_viewport`
  - `Switch_carries_unsaved_model_form_input`
  - `Elapsed_time_continues_across_a_switch`
  - `Every_visible_text_is_English_after_switch`: walk the visual tree of the main pages; no `TextBlock`, tooltip or automation name contains Chinese, except user content and "简体中文".
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the tests and `scripts/test.sh full`. Expected: PASS.
- [ ] **Step 5:** Commit: `Language switch rebuilds the view layer in place`.

### Task 12: Where the user chooses

**Files:**
- Modify:
  - `Views/Pages/SettingsHomeView.axaml` and `ViewModels/Pages/SectionPages.cs` (Appearance section): a "Language" row with a two-item choice
  - `ViewModels/OnboardingViewModel.cs` and its view: the first step shows `English | 简体中文` below the welcome text
  - `UiText.Settings.cs`: `LanguageLabel`, `LanguageEnglish => "English"` and `LanguageChinese => "简体中文"` (identical in both languages, and allowed by Task 2's test), `LanguageSaveFailed`
- Test: `tests/Mindweave.Desktop.Tests/LanguageChoiceUiTests.cs`

- [ ] **Step 1: Write failing tests:**
  - `Choosing_in_settings_switches_and_saves`
  - `Choosing_on_onboarding_rebuilds_the_first_step`
  - `Two_quick_choices_end_in_the_last` (Review Focus 1)
  - `Write_failure_switches_with_a_notice` (Review Focus 4)
  - `First_start_is_English`
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement. The picker calls `LanguageSwitch.SwitchAsync`.
- [ ] **Step 4:** Run the tests. Expected: PASS.
- [ ] **Step 5:** Commit: `Language choice in Settings › Appearance and on the first onboarding step`.

### Task 13: Screenshots, and merge 2

- [ ] Add `LanguageGalleryTests`: the main pages in both languages (exploration with an answer, the summary network, a summary, Settings › Appearance, Settings › Tools, onboarding step 1, a delete dialog). Write them to a gallery folder outside the repo.
- [ ] Fix any clipping (Review Focus 3). Send the screenshots to the user, with one line on the switch fade (none, or a 150 ms cross-fade). The user confirms the look.
- [ ] Merge 2 as in Task 6. STATUS records that English is the default and the user's daily data will open in English.

---

## Merge 3: prompts and model-facing text

### Task 14: Prompt files

**Files:**
- Create: `src/Mindweave.Application/Prompts/en/*.md` and `Prompts/zh/*.md` as embedded resources, and `Prompts/PromptLibrary.cs`
- Modify: the prompt text in `Summaries/SummaryService.cs`, `Conversations/ContextCompressor.cs`, `ConversationTitler.cs`, `ResourceContext.cs`, `RunPrompt.cs`, `Recommendations/RecommendationPrompt.cs`, `SummaryLinks/LinkSuggestionPrompt.cs`, `Workspaces/WorkspaceNameSuggestions.cs`
- Modify: `src/Mindweave.Core/Summaries/SummaryRules.cs`, so the short-name marker also accepts `"Short name:"`
- Test: `tests/Mindweave.Infrastructure.Tests/Languages/PromptLibraryTests.cs`

**Interfaces:**
- Produces: `PromptLibrary.Get(string name) : string` for the current language. Names are the file stems: `summary`, `compression`, `title`, `attachment-digest`, `attachment-digest-short`, `run`, `recommendations`, `link-suggestions`, `workspace-names`. Placeholders are `{name}`.

- [ ] **Step 1: Write failing tests:**
  - `Every_prompt_exists_in_both_languages`
  - `Placeholders_match`
  - `Each_prompt_keeps_the_output_language_rule`: each English prompt contains "language" together with "conversation" or "material", and the Chinese one contains "所用的语言".
  - `Chinese_prompts_are_unchanged`: each Chinese file equals today's string, byte for byte.
  - `Parser_accepts_English_markers`: `"Title: X\nShort name: Y\n\nbody"` parses.
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Move the Chinese text into files verbatim, write the English, and review it like Task 3.
- [ ] **Step 4:** Run the tests and the Chat tests. Expected: PASS.
- [ ] **Step 5:** Commit: `Prompts in one file per language`.

### Task 15: Core's model-facing phrases

**Files:**
- Create: `src/Mindweave.Core/Context/IModelPhrases.cs`, `src/Mindweave.Application/Languages/ModelPhrases.cs`
- Modify: `Core/Context/ResourceMaterial.cs`, `Core/Conversations/ContentBlock.cs:82-166`, `Core/Context/ContextPlanner.cs`, `Core/Branches/BranchContext.cs`, and their callers in Application
- Test: the Core tests pass a Chinese phrase set and keep their expectations. Add `ModelPhrasesTests` in Infrastructure.Tests.

**Interfaces:**
- Produces: `interface IModelPhrases` with these members:
  - `MaterialWhole(string name)`, `MaterialDigest(string name)`, `MaterialDigestAndParts(string name)`
  - `ChunkHeading(int ordinal)`
  - `ImageUnviewable(string name)`, `ImageNotResent(string name)`, `Image(string name)`
  - `AttachmentMarker(string name)`
  - `CitationLabel(CitationKind kind, string title)`, `CitationsHeading`
  - `EarlierSummarized`
  - `QuotedQuestion(string selection, string question)`
  
  Core methods that build these texts take an `IModelPhrases` argument. `ModelPhrases.Current` returns the set for the current language.

- [ ] **Step 1:** Add a Core test using an English phrase set. Expected: build fails.
- [ ] **Step 2:** Implement, then pass `ModelPhrases.Current` from Application. Each Chinese phrase stays byte for byte.
- [ ] **Step 3:** Run `scripts/test.sh full`. Expected: PASS.
- [ ] **Step 4:** Commit: `Core takes model-facing phrases from its caller`.

### Task 16: Tools, web text, and the final guard

**Files:**
- Modify:
  - `Application/Tools/WebTools.cs`, `SearchAttachmentsTool.cs`, `SearchSummariesTool.cs`, `Mcp/McpTool.cs`: descriptions and failure texts through `T` or `BilingualText`
  - `Infrastructure/Tools/GuardedPageFetcher.cs:45`: `Accept-Language` is `en-US,en;q=0.9,zh-CN;q=0.8` or `zh-CN,zh;q=0.9,en;q=0.8`
  - `TavilySearchProvider.cs`, `AddressGuard.cs`, `SdkMcpConnector.cs`
- Modify: `tests/Mindweave.Architecture.Tests/ChineseTextTests.cs` to add `Services_have_no_Chinese_outside_T`. Scan `src/Mindweave.{Core,Application,Infrastructure}`. Chinese is allowed only on lines inside a `T(` / `BilingualText(` call, in `Prompts/zh/`, in `ModelPhrases`, and in comments.
- [ ] **Step 1:** Run the extended guard. Expected: FAIL, with a list.
- [ ] **Step 2:** Convert each site. Tool names and JSON keys stay as they are.
- [ ] **Step 3:** Run the guard and `scripts/test.sh full`. Expected: PASS.
- [ ] **Step 4:** Commit: `Tools and web text in both languages; guard covers services`.

### Task 17: Merge 3

- [ ] Ask the user for a budget for a real-model check of the English prompts (`scripts/real-api.sh eval` with the interface in English). Run it within the budget, or record it as not verified.
- [ ] Merge 3 as in Task 6. STATUS records that the product is bilingual.
