# Interface language: English and Chinese

Basis: step 2 of the user's plan, "upgrade the language capabilities", and the user's decisions of 2026-11-21 (below). The docs are already in English (AGENTS.md "Language and writing"); this design covers the product. Requirements and architecture changes it needs are listed in §9 and committed separately.

## 1. Intent

The user's words, 2026-11-21: make the foundation "purer" — the design should be cleaner, even if it costs more up front, so that later development and the user experience get better.

Decisions:

1. The language switches **without a restart**.
2. The **model prompts** change with it.
3. English is for possible English users, and is a **fully complete version** of the software, not a partial translation.
4. Now, because the later this is done, the higher the cost.
5. The **default is English**.
6. **Existing data is not converted.** For any given user the language of their data is consistent.
7. **Two languages**, English and Chinese, for the foreseeable future.
8. Of the approaches below, the user agreed to: rebuild the window in place on a switch; short text written once with both languages side by side; long prompts in one file per language.
9. The language can also be chosen on the first onboarding screen (§7).

## 2. Goals, non-goals, anti-goals

**Goals**

- Every piece of text the app shows exists in English and in Chinese, and a missing one fails the build or a test.
- Settings › Appearance has a language choice. Choosing applies at once, in place, on the same page.
- With no saved choice, the app is in English.
- Prompts are sent in the interface language. The model keeps writing answers, titles and summaries in the language of the conversation or material, as today.
- Adding a feature later costs one extra argument per string, nothing more.

**Non-goals**

- Converting stored data (titles, messages, error details, attachment names already saved).
- A third language, following the system language, outside translation tools.
- Translating user content or model output.

**Anti-goals** (failure even if it technically works)

- Any text left in the old language after a switch, anywhere.
- English quietly falling behind Chinese as features are added.
- A switch that interrupts a running answer, loses an approval card, a draft or a reading position, or plays a toast or sound twice.
- A rule like "every screen must remember to refresh on a language change".

## 3. Approaches

**How a switch reaches the screen.**

| | What the user sees | Up-front cost | Risk | Cost to every later feature |
| --- | --- | --- | --- | --- |
| Live labels | Words swap in place | High: about 570 XAML uses become bindings, and about 560 places that build text in view models must re-raise | Stale text in corners that forgot to refresh | Permanent: every new string in a view model must be written the live way |
| **Rebuild in place (chosen)** | The window content redraws within about half a second on the same page | Moderate, structural, once (§6) | Losing transient state; mitigated by §6.3–6.5 | None |
| Restart | "Restart to apply" | Lowest | A running answer is interrupted | None |

Research (2026-11-21) on Starward (WinUI), Ryubing/Ryujinx, PixiEditor, v2rayN and SourceGit (Avalonia): every app that switches live has stale-text bugs in text built in code (PixiEditor has open "needs a restart" issues; Starward pages refresh only if they subscribe); v2rayN, which reads text the way Mindweave does today, requires a restart. None of them checks that every string exists in each language, or that parameters match. Ryubing keeps all languages side by side in each entry, which is the model for §4.

**How text is written.**

| | Missing language | Parameters and plurals | Third language | Chosen |
| --- | --- | --- | --- | --- |
| **Side by side in C#: `T(en, zh)`** | Does not compile | Ordinary C# per language | Mechanical rewrite of every line | Yes (two languages decided) |
| One class per language | Does not compile | Ordinary C# | Add a class | No: three places per string, languages not side by side in review |
| .resx / JSON per language | Silent fallback unless a test checks | `{0}` templates, checked at run time | Add a file; tool-friendly | No |
| Avalonia resource dictionaries | Silent fallback | Hard from C# | Add a file | No: a second system for C# text |

## 4. Text

**4.1 The language.** `UiLanguage` (`English`, `Chinese`) lives in Application, so both the interface and the prompts read it. The current language is one process-wide value, set at startup before any view is built and changed only by a switch (§6).

**Where the choice is stored (builder's call, 2026-11-21).** Not in the database: a small file in the channel's data folder, `language` (content `en` or `zh-Hans`; missing, unreadable or unknown means English). Reasons: the database opens only after the first screen ("opening data…") is shown, and the recovery screen and the startup failure window appear without it, so a database preference would show those screens in English and then redraw; and a database preference would travel inside backups, so restoring one could silently change the language. A file outside the database is read before any view and is not touched by restore. If writing it fails, the switch still applies for this run (§7).

**Core holds no language state (builder's call).** Core is free of shared runtime state today and stays so. Text that Core builds only for display (citation labels, the default pasted-text name) moves to `UiText`. Text that Core builds for the model (material headers, image placeholders, attachment markers, the citation list, the preamble for summarized earlier messages, the quoted question of a branch) takes a phrase set as an argument, which Application passes in for the current language.

**4.2 Writing a string.** Every member of `UiText` becomes a static property or method that returns the current language:

```csharp
public static string Cancel => T("Cancel", "取消");
public static string Conversations(int n) => T(Plural(n, "conversation", "conversations"), $"{n} 个会话");
```

- No `const` and no cached `{ get; } =` statics remain in `UiText`: they would keep the language they were first read in.
- `x:Static` in XAML stays as it is. It reads the property when a view is built, and a switch rebuilds the views.
- Punctuation and spacing belong to each language's string (Chinese full-width marks; English sentence case, no full stop on short labels).
- Number and date formatting uses the language's culture (`en-US`, `zh-CN`) through one `UiText.Culture`.
- English wording follows the architecture §2.3 glossary (summary, summary network, branch, anchor, citation, workspace, …). Chinese wording stays the requirement terms, as today.
- Language names in the picker are never translated: "English", "简体中文".

**4.3 Text that is not in `UiText` today.** It moves in:

- Desktop strings outside `Text/` (for example the attachment chip's accessible name, the default pasted-image name, the composer's accessible name, list separators).
- Text produced in Application or Infrastructure and shown to the user (MCP server status, storage refusals, web fetch and guard errors, citation labels). These return a kind or code; `UiText` turns it into words. Where a service must produce words itself (model-facing text, §5), it uses the same `T` from Application.
- Error details already written to the database stay in the language they were written in (decision 6).

## 5. Prompts and model-facing text

- **Long prompts** (summary, context compression, title, attachment digest, recommendations, summary link suggestions, workspace name suggestions, run instructions) move to embedded files, one per language: `src/Mindweave.Application/Prompts/en/<name>.md` and `…/zh/<name>.md`. A test fails if a prompt exists in only one language or if their placeholders differ.
- **Short model-facing text** (tool descriptions, material headers, "[Attachment: name]" markers, citation labels sent to the model) uses `T(en, zh)` in code.
- The prompt language is the interface language at the moment the request is built. Every prompt keeps the instruction to write in the language of the conversation or material, so a Chinese conversation in an English interface still gets Chinese titles and summaries.
- Parsing of model output accepts both languages' markers (the summary parser already accepts "标题：" and "Title:"; short-name and other markers get the same treatment).
- Tool names (`web_search`, `fetch_page`, …) and stored keys do not change.
- The web fetch `Accept-Language` header follows the interface language.
- New text written into data (attachment markers in message text, step notes) is written in the interface language at that time.

## 6. The switch: rebuilding the view layer in place

**6.1 What exists.** The in-app backup restore already replaces the window's content without a restart, but it rebuilds every service. The language switch rebuilds only the view layer and keeps every service, so running work goes on.

**6.2 Two providers.** Composition splits into:

- a **root provider**: Infrastructure, Application, platform services (theme, dispatcher, system shell, alert sound), the request bridges, and `SummaryNetworkSessions` (so unsaved layouts and the viewport survive);
- a **view provider**, built from the root and replaced on a switch: `RootViewModel`, `ShellViewModel`, `Workbench`, `ConversationListViewModel`, `WorkspaceNewsViewModel`, the dialog view models, `ExitCoordinator`, `AppStartup`.

Root services are registered in the view provider as existing instances, so disposing the view provider never disposes them. No Application service is created twice.

**6.3 Disposal.** Every view-layer singleton that subscribes to a service unsubscribes in `Dispose` (today several subscribe with lambdas and never unsubscribe; dropping them without this would leak and, for workspace news, play a toast and sound twice). Disposing the view provider disposes them, the same way `Workbench.Release` already disposes pages. A test checks that after a switch each service event has the same number of handlers as before.

**6.4 Where the user was.** Before the switch, a snapshot is taken in memory: the active workspace; each workspace's section and route stack; the settings subpage and its scroll position; whether a summary is in edit mode. The old view tree is detached first, so conversation views save their reading position and drafts. The new view provider is built, the snapshot is replayed through the new `Workbench`, and the new root view is put in the window. The window itself (size, position) stays. The startup path is **not** rerun: it marks running answers as interrupted.

**6.5 Running work.** Answers, runs, tool calls, pending approvals and summary generation live in `GenerationCoordinator`; new pages reattach through `Observe`, as when the user switches conversations and back. The elapsed-time display is computed from the run's start time, not counted by the old view model. MCP servers keep running. Attachments still being stored when the switch is chosen are awaited (they take a moment) before the rebuild.

**6.6 When a switch waits.** The picker is on the Settings page, so most flows cannot be open at the same time. If one is (a dialog, the layout prompt, exit, a backup flow, a question being sent), the switch waits until it closes, and the picker says so. It waits at most about 10 seconds, and not at all once the reader leaves the page they chose on; then the language in use stays and the stored choice is set back to it. A rebuild that fails also leaves the old view and language, writes the old choice back, and says so beside the picker. If another settings form on the page has unsaved input (model connection form, add-MCP-server form, web search key box), its fields are carried over into the rebuilt form.

**6.7 The window.** Everything with text that `MainWindow` holds today (overlays and dialogs, 27 strings) moves into a root view that is rebuilt. Code that looks up services through the session (exception guard, shutdown, startup) goes through the root provider.

**6.8 What the user sees.** The content redraws on the same Settings section with the language row in view. Whether a short cross-fade is used is a look-and-feel choice shown to the user with the screenshots.

## 7. Settings and first start

- **Settings › Appearance** gains a "Language" row: "English" / "简体中文". Choosing saves the preference and switches. If saving fails, the switch still applies for this run, with the same notice wording as a failed appearance save.
- **First start** with no preference: English. The user's own daily data, which has no preference yet, also opens in English until switched (decision 5).
- The startup, recovery and startup failure screens use the stored choice, since it is read before them (§4.1).
- **The first onboarding screen** also has the language choice (user, 2026-11-21), as a small "English | 简体中文" control below the welcome text, before anything is filled in. Choosing saves the choice and rebuilds that first screen in the new language; since nothing has been entered yet, nothing is carried over. Later onboarding steps have no picker. The look is confirmed with the screenshots.

## 8. Tests

- **Pinned language.** The language is process-wide, so each test class runs in one language. Existing tests run in Chinese and keep their assertions. Classes that use English or switch languages run in a collection that is not parallel with the others.
- **Completeness.** Under English, every `UiText` property and method (methods called with sample arguments) returns text with no Chinese characters, which also catches swapped arguments. Under Chinese, every member returns non-empty text. Every prompt exists in both languages with the same placeholders.
- **The switch.** Switching while an answer streams keeps it streaming and keeps a pending approval card; the draft, reading position, settings subpage and summary network viewport survive; no service event gains handlers; no toast or sound is repeated.
- **Screens.** The gallery tests render the main pages in both languages, for the user's review (§10).

## 9. Spec changes (separate commit; the user confirms before merging)

- **Requirements v0.24** (v0.23 is the output limit): a new §10.5 "Interface language" with the user decisions above, and acceptance rows in §11 (switch in place, default English, prompts follow, running answer survives the switch).
- **Architecture:** a new subsection under §3 "Interface language" (the two providers, text written side by side, prompts per language); §2.3 glossary gains "interface language (界面语言) → `UiLanguage`".
- **AGENTS.md** "Naming and structure" rule 1: English UI text uses the glossary's English wording, as Chinese UI text uses the requirement terms.

## 10. Order of work

Three merges, each with the full suite and CI green on Linux and Windows:

1. **Text mechanism and English text.** `UiLanguage`, `T`, the conversion of every `UiText` member, the strings moved in from elsewhere (§4.3), and the English text, translated by agents in batches by `UiText` file, each batch checked by an independent reviewer against the glossary. The app still starts in Chinese and has no picker, so nothing visible changes for the user yet.
2. **The switch.** The two providers, disposal, snapshot and replay, the Settings row and the onboarding choice, English as the default, the switch tests, and screenshots of both languages. The user confirms the look before this merges.
3. **Prompts.** Prompt files in both languages, short model-facing text, `Accept-Language`. Fake-model tests check placeholders and parsing; a real-model check of the English prompts needs the user's budget.

Conflicts: `task/session-network` adds strings to `UiText.SummaryNetwork.cs`. Whichever merges second converts or adds the other's strings; that session and this one agree the order before merge 1.

## 11. Left to the builder

- Exact names and folders (glossary first, per AGENTS.md).
- How `T` reads the current language, as long as it is one value set by the switch.
- The English wording of each string, within the glossary.
- How the snapshot records routes, within §6.4.

## 12. Not verified yet

- How much longer English labels are, and where layouts need room: checked with the screenshots in merge 2.
- The English prompts with real models (paid; merge 3).
- Real Windows hardware.
