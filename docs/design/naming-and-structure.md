# Mindweave code structure and naming guidelines

## Adopted: the lightweight version (agreed with the user 2026-11-13)

Rules over checks: short rules that agents load every session, plus one fix to an existing test.
Status (2026-11-15): the code changes in C are done on `track/claude-auto`. A and B are proposals for `main`
(`AGENTS.md`, `docs/architecture.md`) and wait for the user's decision; until then they apply to this track only.

### A. Proposed section for `AGENTS.md` (on `main`; final wording is the user's call)

> **命名与结构 / Naming and structure**
> 1. **One concept, one name, taken from the requirements.** Use the code name in the glossary
>    (`docs/architecture.md`) for namespaces, folders, types, resource keys and `UiText` members. Chinese UI text uses
>    the requirements term word for word. A glossary word is not used in any other sense: `Network` means real
>    networking only. A new concept gets a glossary row before it gets a folder.
> 2. **Saved names never change with code renames.** Table and column names, preference keys and saved page/section
>    keys stay as they are. Renaming one is a migration and needs the user's approval.
> 3. **Folder = namespace.** The Desktop project root holds only start-up and wiring.
> 4. Follow the existing folder and suffix patterns. Ask before creating a new top-level folder.

### B. Glossary for `docs/architecture.md` (on `main`; check each row against the requirements first)

| Requirements term | Code name |
| --- | --- |
| 工作区 | `Workspace` |
| 会话 | `Conversation` |
| 分支 | `Branch` |
| 总结 / 总结版本 | `Summary` / `SummaryVersion` |
| 总结网络 | `SummaryNetwork` |
| 推荐问题 / 推荐节点 | `Recommendation`, `RecommendedQuestion`, `RecommendedNode` |
| 上下文总结 | `ContextSummary` |
| 阅读位置 / 已读 | `Reading`, `ReadingState` |
| 模型设置 / 凭据 | `ModelSettings`, `Credential` |
| 探索 / 设置 (主区) | `Explore`, `Settings` |
| Reserved | `Network` = real networking only; `Graph` = graph algorithms only |

### C. Work queue (after the health-check fixes)

| Order | Item | Notes |
| --- | --- | --- |
| 1 | F7: 知识网络 → 总结网络 in `UiText.WorkspacesIntro` | **Done** (5a0f8ff). The summary prompt (`SummaryService`) still says 知识网络: it is model-facing text, left unchanged |
| 2 | F6: rename `Network` → `SummaryNetwork` in all layers, as one dedicated commit | **Done** (04bf858). Saved `"network"` section key and `GenerationErrorKind.Network` kept; key pinned by `MainSectionKeyTests` (3556ac5). Local variables named `network` were left as they are. Checked with the full suite and a before/after gallery comparison in both themes |
| 3 | A1: invert `ViewModelBoundaryTests` to check every Desktop namespace except the view layer | **Done** (68f7da9). No existing offenders |
| 4 | F2/F3: `UiText*.cs` → `Text/`; `PageFactory` → `Navigation/` | **Done** (ee7a3e3). `UiText` is in namespace `Mindweave.Desktop.Text` (global using in C#, `xmlns:t` in XAML), so folder = namespace holds |
| 5 | Check whether IDE0130 (namespace must match folder) runs during the build with existing settings | **Done** (`.editorconfig`): it runs and fails the build for `src/**`. Tests are left out: helpers under `Support/` share their test namespace on purpose |
| — | F4/F5 | Only when those files are being edited anyway |

### D. When to revisit the deferred parts

Add the matching deferred item only when a trigger happens:
- An ambiguous name creeps back → A3.
- A rename breaks saved state → A5.
- A missing resource key reaches a build you use → A6.
- More people start working on the code → reconsider §4–§6 and §10.

---

# Deferred reference (not adopted)

Everything below is the full v2 draft, kept for reference only. It is **not** a rule set. Use it when a revisit
trigger above fires, or as the rename procedure (§10) for F6.

Status: reference. Based on `track/claude-auto` at `c89899c` (2026-11-13).
Scope: every project under `src/` and `tests/`. `docs/architecture.md` does not cover folder layout or naming
today. These rules are meant to become a section of it, or a sibling doc on `main`; that is the user's decision
(AGENTS.md: design rules shared by both tracks live on `main`).

Accepted decisions so far: rename the summary network to `SummaryNetwork` in every layer (F6) and fix the
知识网络 wording (F7).

---

## 1. Principles

| # | Principle | The trap it prevents |
| --- | --- | --- |
| P1 | **One concept, one name, everywhere.** Requirements, UI text, namespaces, types, tests and resource keys use the same term. | `Network` meaning both the 总结网络 and a lost connection |
| P2 | **Names come from the glossary (§2), not from whoever writes the first file.** A new concept gets a glossary entry before it gets a folder. | Synonyms drifting apart (总结网络 / 知识网络 / 网络图) |
| P3 | **A name must make sense with its namespace stripped.** Tests, `ViewLocator` and `CompositionRoot` import many namespaces at once, so `Layout`, `Session` or `Node` alone tell the reader nothing. | `NetworkLayout` vs any other layout |
| P4 | **Structure follows the layers.** Where a file lives says which layer owns it and what it may depend on. | Logic leaking across layers unnoticed |
| P5 | **Stored names are a contract.** Anything written to disk (tables, columns, preference keys, route keys) never changes when the code is renamed. | A rename silently resetting the user's saved state |
| P6 | **Rules are checked by tests, not by memory.** A rule that can be checked mechanically gets an architecture test (§9). | Allow-lists that new code quietly escapes (F1) |

---

## 2. Glossary: the code vocabulary

One row per product concept. The **requirements term** is the source of truth; the **code name** is used for
namespaces, folders, types, resource keys and `UiText` members. Entries below are taken from requirements §4
and the existing folders. Before adoption, check each one against the requirements.

| Requirements term | Code name | Notes |
| --- | --- | --- |
| 工作区 | `Workspace` | |
| 会话 | `Conversation` | |
| 分支 | `Branch` | |
| 总结 / 总结版本 | `Summary` / `SummaryVersion` | |
| 总结网络 | `SummaryNetwork` | Replaces bare `Network` (F6). Its parts are `SummaryNetworkNode`, `...Edge`, `...Layout`, `...Canvas` |
| 推荐问题 / 推荐节点 | `Recommendation`, `RecommendedQuestion`, `RecommendedNode` | |
| 上下文总结 | `ContextSummary` | Internal, never shown as user content |
| 阅读位置 / 已读 | `Reading`, `ReadingState` | |
| 模型设置 / 凭据 | `ModelSettings`, `Credential` | |
| 探索 / 设置 (主区) | `Explore`, `Settings` | Values of `MainSection` |

**Reserved words.** These may be used only in the sense given:

| Word | Allowed only for | Use instead for anything else |
| --- | --- | --- |
| `Network` | Real networking: connections, HTTP, `GenerationErrorKind.Network` | `SummaryNetwork` |
| `Graph` | Generic graph algorithms inside the layout code (node and edge sets) | `SummaryNetwork` for the product feature |
| `Node`, `Edge` | Parts of the summary network, always qualified outside its namespace | `Item`, `Entry` elsewhere |
| `Session` | A runtime object that lives while a page or workspace is open | Never for stored data |
| `Settings` | User-changeable preferences, stored | `Options` for configuration passed in code |

**Banned suffixes and names** for new types: `Manager`, `Helper`, `Utils`, `Misc`, `Common`, `Data`, `Info`.
They hide what a type does. Name the job instead (`...Formatter`, `...Placement`, `...Reconciler`).

**Adding a term.** Add the row here and record it in `TRACK.md`; if requirements lack the term, propose a
requirements change on `main`. The Chinese UI text uses the requirements term word for word (lesson of F7).

---

## 3. Layers and where code lives

| Project | Owns | Grouped by | May depend on (enforced by `LayeringTests`) |
| --- | --- | --- | --- |
| `Mindweave.Core` | Domain types and rules, no I/O | Concept (`Conversations/`, `Branches/`, `Summaries/` ...) | Runtime only |
| `Mindweave.Application` | Use cases, services, and the **ports** other layers implement | Concept; store ports together in `Storage/` | Core, logging abstractions |
| `Mindweave.Infrastructure` | Adapters: SQLite, AI clients, credentials, hosting | Technology (`Sqlite/`, `Ai/`, `Credentials/`) | Core, Application; never Avalonia |
| `Mindweave.Desktop` | Avalonia UI | Role first, then area (§4) | Everything; Infrastructure only in the composition files |

Placement rules:
- A **port** (an interface another layer implements) lives in the layer that consumes it: store ports in
  `Application/Storage/I<Concept>Store.cs`, implemented by `Infrastructure/Sqlite/Sqlite<Concept>Store.cs`.
- An interface with **only one implementation in the same project** lives next to that implementation
  (the reason for moving `PageFactory` to `Navigation/`, F3).
- A concept present in several layers uses **the same folder name in each** (`Application/SummaryNetwork/`,
  `Desktop/SummaryNetwork/`).

---

## 4. Folders

1. **Folder = namespace.** `Foo/Bar/X.cs` declares `namespace Mindweave.<Project>.Foo.Bar`.
2. **Folder names come from the glossary.** Collections of things are plural (`Conversations/`, `Workspaces/`);
   a feature or capability is singular (`Storage/`, `SummaryNetwork/`). Existing names are not changed only for grammar.
3. **No empty or speculative folders** (AGENTS.md). A folder is created by the first file that needs it.
4. **Three files make a folder.** One or two related files stay where they are until a third joins them.
5. **At most three levels below the project root** (`Views/Pages/X.axaml` is two). Deeper means the area should be split.
6. **The Desktop root holds start-up and wiring only:** `Program`, `App`, `AppHost`, `CompositionRoot`, `ViewLocator`,
   `Converters`, csproj and manifest.

### Desktop layout

| Folder | Holds |
| --- | --- |
| root | Start-up and wiring (rule 6) |
| `Text/` | `UiText.cs` + `UiText.<Area>.cs` partials, namespace `Mindweave.Desktop` (F2) |
| `ViewModels/`, `ViewModels/<Area>/` | Shell-level view models; per-area view models (`Pages/`, `Conversations/`) |
| `Views/`, `Views/<Area>/` | The matching views, mirroring the view model's area |
| `Controls/` | Custom Avalonia controls used by more than one view (F4) |
| `Navigation/` | Routes, sections, workbench, page factory (interface and implementation) |
| `SummaryNetwork/` | Presentation logic for the summary network shared by view models and views |
| `Markdown/` | Markdig extensions and Markdown rendering |
| `Services/` | Desktop UI ports and their implementations (dispatcher, shell, theme, exception guard) |
| `Themes/` | Design tokens only: colours, icons |
| `Styles/` | Control styles, `Styles/<Area>.axaml` plus shared `Controls.axaml`, `Motion.axaml` |

**Placing a new Desktop file**, first match wins:
1. Start-up or DI wiring → root.
2. User-visible text → `Text/UiText.<Area>.cs`.
3. View model, or logic only view models use → `ViewModels/<Area>/` or a presentation folder such as `SummaryNetwork/`.
4. `.axaml` view and code-behind → `Views/<Area>/`, mirroring its view model.
5. Avalonia control or drawing helper used by one view → next to that view; by several → `Controls/`.
6. Desktop port or platform adapter → `Services/`, interface and implementation together.

---

## 5. Files and types

- **One top-level type per file; the file name is the type name.** Allowed exceptions:
  partial classes split by area (`UiText.Conversation.cs`, `RecommendationService.Nodes.cs`), and a small group of
  records belonging to one type (`<Type>Records.cs`).
- **Suffixes say the role.** Use these and keep their meaning:

| Suffix | Meaning | Example |
| --- | --- | --- |
| `I<X>Store` / `Sqlite<X>Store` | Persistence port / its SQLite adapter | `ISummaryStore`, `SqliteSummaryStore` |
| `<X>Service` | Application-layer entry point for one concept | `ConversationService` |
| `<X>Coordinator` | Orchestrates several services over a multi-step flow | `GenerationCoordinator`, `ExitCoordinator` |
| `<X>ViewModel` / `<X>View` | A pair: same `<X>`, same area folder, registered in `ViewLocator` | `SummaryViewModel` / `SummaryView` |
| `<X>Route` | A navigation target | `SummaryRoute` |
| `<X>Options` / `<X>Settings` | Configuration passed in code / user preferences that are stored | |
| `<X>Exception` | Exception type | `StorageWriteException` |
| `<X>Tests` / `<X>UiTests` | Unit tests / headless UI tests for `<X>` | |

- **Qualify generic nouns** (P3): `SummaryNetworkLayout`, not `Layout`; `WorkspaceToast`, not `Toast`.
- **Enum members are read with their type** (`GenerationErrorKind.Network` is fine), but a new enum member must
  not reuse a glossary word in another sense.

---

## 6. XAML and resources

- **Resource keys:** `<Area><Role>`, with the glossary area as prefix: `SummaryNetworkNode`, `SummaryNetworkEdgeIndirect`.
  Icons: `Icon<Concept>`. Shared, area-less tokens keep plain names (`AccentBrush`).
- **Control names used by tests or automation:** `<Role><Concept>` (`NavSummaryNetwork`).
- **`x:Class` and `x:DataType` follow the folder and namespace rules.** A moved `.axaml` file moves with its code-behind.
- `{DynamicResource}` keys are not checked by the compiler: a missing key gives a missing colour at runtime.
  This is why test A6 (§9) exists.

---

## 7. User-visible text

- All user-visible text lives in `UiText` (Desktop) or in the model prompt sources (Application). There are no string literals in views.
- `UiText` member names use glossary code names (`SummaryNetworkTitle`), so a search for a concept finds its text too.
- The Chinese text uses the requirements term word for word. Two wordings for one concept is a bug (F7).

---

## 8. Stored names: a stable contract

- **Database:** tables and columns are `snake_case`, named for the stored data, not the screen (`node_layout`
  is correct). Renaming a table or column is a schema migration, so it follows the migration rules and needs the user's approval.
- **Preference keys:** `area.name` in lowercase dot/kebab style, with `:<id>` for per-item keys
  (`recommend.nodes.plan:<workspace>`). Existing `snake_case` keys (`last_workspace_id`, `active_model_config_id`)
  stay as they are. Renaming a stored key is a data migration, not a cleanup.
- **Route and section keys** (`MainSection.ToKey`) are stored as the last location, so they are stored names too.
- **Each stored string is defined once,** in a `const` next to the code that reads it, and a test pins its literal value (A5).
  Code renames never change these strings. For example, after F6 `MainSection.SummaryNetwork` still stores `"network"`.

---

## 9. Enforcement: architecture tests

Existing tests: `LayeringTests` (project dependencies), `SeamTests` (credential and vendor seams),
`ViewModelBoundaryTests` (view models use interfaces and expose no Avalonia types), plus the `ViewLocator`
registration test. Proposed additions, cheapest first:

| ID | Test | Catches | Cost |
| --- | --- | --- | --- |
| A1 | Invert `ViewModelBoundaryTests`: check every Desktop namespace except view-layer ones (`Views`, `Controls`, `Markdown`, root) | New view-model namespaces escaping the checks (F1) | Small; may surface existing offenders |
| A2 | Folder = namespace: scan `src/**/*.cs` and compare the path with the `namespace` line | Moved files with stale namespaces | Small |
| A3 | Reserved and banned names: no type or namespace containing `Network` outside an allow-list (`GenerationErrorKind`), and no `Manager`/`Helper`/`Utils`/`Misc` suffixes | The F6 trap coming back | Small, after F6 |
| A4 | One top-level type per file, file name = type name, with the §5 exceptions | Hidden types, hard-to-find code | Small–medium |
| A5 | Stored-name snapshot: every preference key, section key and route key constant equals a pinned literal | Renames that reset saved state (P5) | Small |
| A6 | Every `{StaticResource}`/`{DynamicResource}` key used in `.axaml` and code resolves in `Themes/` or `Styles/` | Missing colours after renames (runtime-only failures) | Medium; check what existing palette tests already cover |

The tests replace review vigilance only where they apply; §10's checklist covers the rest.

---

## 10. Process

**Introducing a concept:** glossary row (§2) → requirements term confirmed → folder in each layer that
needs it → `UiText.<Area>.cs` → resource-key prefix. All of these use the same code name.

**Renaming a concept** (the F6 procedure, reusable):
1. Search for every sense of the word in all layers, tests, XAML, docs and stored strings, and list which senses must *not* change.
2. Keep stored names (P5). Rename code only.
3. Do it in one dedicated commit, separate from other work, when no other session edits the files involved.
4. Build and run all tests. Then open every affected page on the dev channel (runtime-only failures) and use
   `scripts/daily.sh trial` for anything reading saved state.
5. Record the change in `TRACK.md`. Doc corrections for `main` are the user's decision.

**Review checklist for any new file or type:**
1. Is every concept word in the glossary, and used in that sense?
2. Does the name make sense with the namespace stripped?
3. Do the folder and namespace match, in the layer that owns it?
4. Does it write a stored name? If so, is that a pinned constant?
5. Does it add a namespace that an architecture test lists by name?

---

## 11. Existing exceptions (kept)

- `snake_case` preference keys in Infrastructure (§8).
- `prototypes/network-layout` and history docs keep their names; they are records, not live code.
- `Application/Storage/` groups store ports by role rather than concept. This is deliberate: it is the persistence boundary.

---

## 12. Backlog (to do after the health-check fixes)

| # | Item | Status |
| --- | --- | --- |
| F1 / A1 | Invert the view-model boundary test | Proposed |
| F2 | `UiText*.cs` → `Text/` | Proposed |
| F3 | `PageFactory` → `Navigation/` | Proposed |
| F4 | Reusable controls → `Controls/`, when touched | Proposed |
| F5 | Check `BackupFormat`, `Motion`, `TreeIndent` in `Views/` | Proposed |
| F6 | Rename `Network` → `SummaryNetwork` in all layers; keep stored `"network"` | **Accepted** |
| F7 | 知识网络 → 总结网络 in `UiText.WorkspacesIntro` | **Accepted** |
| A2–A6 | New architecture tests | Proposed; A3 after F6 |

Suggested order: F7 → F6 → A3 and A5 → F2/F3 → A1, A2, A4, A6 → F4/F5 as files are touched.
