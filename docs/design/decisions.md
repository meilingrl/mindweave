# Decision log

Handover §4 lets the implementer decide certain choices, which must stay adjustable. Each entry states the choice, the reason, and when to reconsider it.

## D-001 Runtime and dependency versions (2026-10-25)

.NET 10 (LTS), Avalonia 12.1.4, LiveMarkdown.Avalonia 2.5.1 (introduced in S2), CommunityToolkit.Mvvm 8.4.2, Microsoft.Extensions.DependencyInjection/Logging 10.0.13, Microsoft.Data.Sqlite 10.0.13, xunit.v3 3.2.2 + Avalonia.Headless.XUnit 12.1.4. LiveMarkdown 2.5.1 requires Avalonia 12; the sample app has been verified on this combination, and the test combination is the same as the sample's. All versions are kept in one place, `Directory.Packages.props`.

## D-002 Primary keys and time (2026-10-25)

UUIDv7 text primary keys; time as UTC Unix milliseconds. See [data relationships](data-model.md).

## D-003 Synchronous level (2026-10-25)

WAL + `synchronous=FULL`. With NORMAL, a process crash also loses no committed transaction; only a power loss may lose the last few. This app writes sparsely, so the cost of FULL is negligible, and in return we get the simple promise "saved means on disk". Re-evaluate if the S1 checkpoint frequency causes noticeable stalls.

## D-004 All migrations in a single transaction (2026-10-25)

All pending migrations commit in one transaction, so the database never stops at an intermediate version. The cost is that migration steps may only use statements that can run inside a transaction (architecture §12.2 already restricts them this way).

## D-005 Keep 5 migration backups (2026-10-25)

This applies only to backups made before a migration; routine backups are not in the scope of the first release.

## D-006 Reads run inside a single snapshot (2026-10-27)

`SqliteDatabase.ReadAsync` does all its work in one read transaction. Once message content is split into two tables, message rows and content blocks, multi-statement reads must be consistent. Putting this in the database layer instead of in each query means that later multi-table reads (branches, summaries) get the guarantee automatically. Under WAL a read transaction does not block writes; the only cost is that a snapshot is kept for the duration of the read.

## D-007 Trace writes are best-effort (2026-10-27)

A failed `ai_request` write is only logged. It does not affect the answer's final state and does not enter the storage health state. Losing a diagnostic record is acceptable; failing to save an answer because of diagnostics is not. Re-evaluate if the D2 diagnostics UI needs "the record always exists".

## D-008 Finished answers are parsed whole; answers being generated are built incrementally (2026-10-27)

LiveMarkdown takes two kinds of input: the whole text, `MarkdownDocumentUpdate.Full`, and the incremental `ObservableStringBuilder` (parsed in the background, then the control is updated incrementally). Finished answers are always parsed whole: the layout depends only on the text and the rendering configuration, so after a restart anchors are restored against the same layout. An answer being generated only grows by appending, so it is built incrementally, which avoids rebuilding the whole control each time. When an answer is written in its final state, the page swaps in a new message item, and the view switches from incremental to whole with it.

## D-009 Questions in child conversations put the quotation first in what is sent (2026-10-27)

For the first question of each branch conversation on the chain, the material sent starts with “关于上文中的这段内容：” ("About this passage above:") followed by the selected text, quoted line by line. The stored question stays in the user's own words. Only the first question gets the prefix: later questions are already in the same context, and repeating the quotation wastes budget. The title request carries the quotation too, so the branch title points at the passage being explored.

## D-010 The reading position is recorded at the character on the "reading line", with an in-line offset (2026-10-27)

The reading line is 8 px below the top of the viewport. The record holds the message ID at that point, the source text block (the block range, as for anchors, plus an index), the character position, and the distance from the reading line to the top of that character. For a question, or a place without text, only the offset from the top of the message is recorded. Recording only the character makes a restore snap to the start of the line, off by up to one line (10 px measured); with the offset, a restore under the same layout is exact. When the layout changes, the position returns to the character if it still exists, to the top of the message if the block does not, and to the end if the message does not.

## D-011 Token estimates: counted by character, deliberately high, calibrated by measurement (2026-10-27)

We do not bring in each model's tokenizer (large, and it changes with the model). Each CJK or other non-ASCII character counts as 1 token, every 3 ASCII characters as 1 token, plus 4 per message and 16 per request. Calibration uses the `prompt_tokens` returned by DeepSeek's official service: across 12 real requests, the estimate for the larger requests was about 1.4–1.5 times the actual count. When each request first added only 3, the smallest request was underestimated (estimate 56, actual 62); after changing it to 16, no estimate was below the actual count. Estimating high costs a slightly earlier compression; estimating low gets the request rejected by the service. The real-service tests keep checking that "the estimate is not below the billed count".

## D-012 Compression runs in the answer task's own execution slot (2026-10-27)

The context summary request is not queued as a separate task. It is a preliminary step of the answer task: in the same execution slot, first the context summary, then the answer. This way there is no deadlock where "the answer holds the slot while waiting for its own context summary task", the global limit of two concurrent tasks is unchanged, and stopping the answer also stops the context summary. The context summary request is recorded in `ai_request` under the answer task, so `generation_task.kind` needs no new type (it has a CHECK constraint; adding a type means rebuilding the table).

## D-013 Only parse usage the service returns on its own; do not request it (2026-10-27)

Not every compatible service accepts the Chat Completions `stream_options.include_usage`, and sending an unknown field may get the whole request rejected. `usage` is recorded in `ai_request` only when the service returns it on its own, to check the estimates and for diagnostics.

(Changed to requesting it on 2026-11-13, health check gap-unprompted-1.) The statistics page (item 6) and the per-model token calibration both depend on usage, and services that follow OpenAI's semantics return it only when `stream_options.include_usage` is requested. It is now requested by default. When a service rejects it with a 400 because it does not recognize the field (the error message mentions `stream_options`), the same request is sent again without it. The official OpenAI API has still not been tested directly.

## D-014 Formulas use LiveMarkdown.Avalonia.Math 2.5.1 (RaTeX) (2026-10-27)

During the S3 trial the user pointed out that LaTeX was not rendered. LiveMarkdown itself has no formula support; the companion package by the same author, `LiveMarkdown.Avalonia.Math`, uses RaTeX to render inline `$…$` and block `$$…$$`. RaTeX ships a native library covering macOS arm64/x64, Windows x64 and Linux (CI); the licenses are Apache-2.0, MIT, and SIL OFL for the KaTeX fonts. In the layout a formula is a single object placeholder character, so anchors work as usual. When quoted text is captured, the placeholder characters are restored in order to that block's LaTeX source, so quotations sent to the model do not lose formulas. The rendering configuration identity changes to `livemarkdown-2.5.1/math-1`. Anchors with the old identity are still restored where possible: validation is based on the content and layout hashes, so answers without formulas keep the same layout, and answers with formulas that fail validation degrade according to MW-C13.

## D-015 Output limit for context summaries and one internal retry (2026-10-27)

One compression failed during a trial. The same request from the trial database was replayed 8 times (`deepseek-flash`, limit 4,096): reasoning used 718–4,096 tokens, and 2 runs hit the limit, one with no text at all and one with only a truncated context summary of 179 characters (the old code would have used it as a success). The fix: the limit becomes 16,384 (3 measured runs of the same request used 533–2,677 for reasoning); a context summary is used only when the service finished normally (not `length`) and it is not empty; when it is truncated or empty, it is requested once more inside the same answer task. What is retried is a preliminary step inside the task, not a resend of the user's question, so it does not conflict with "no automatic resend after a restart".

## D-016 Formulas also recognize backslash delimiters (2026-10-27)

When S3 was retested, the model wrote a block formula as `\[ … \]`, and the UI showed "[" followed by the LaTeX source: Markdown reads `\[` as an escaped square bracket. Many models use `\(…\)` and `\[…\]` by default, so the shared parsing pipeline enables the formula package's own `UseExtendedMathematics`, and all four kinds of delimiters render as formulas (streaming and finished answers use the same pipeline). The answer text is not rewritten, so anchor offsets are not affected; when quotations restore LaTeX, they recognize these two delimiters too. The cost is that a literal `\[` square bracket in the text will be taken as a formula, which is very rare in chat answers. The rendering configuration identity changes to `livemarkdown-2.5.1/math-2`; anchors with `math-1` are still restored where possible, and answers containing backslash formulas degrade according to MW-C13.

## D-017 Read status, reading position and input for summaries (2026-10-28)

- **Read:** the largest share of the text that has entered the viewport reaches 90%, and the version has been shown for a full 8 seconds during this opening (the "threshold, and dwell time for short text" that handover §4 delegated). Both are required, so a short text that fits on one screen is not "read as soon as it opens", while reading a long text normally takes well over 8 seconds. The manual toggle can be changed at any time; a version saved after a manual edit is marked as read.
- **Reading position:** summaries store a scroll position per version, not an anchor locator as conversations do. Summaries have no "branch from selection" (MW-C04 does not offer it in the first release), so they need no verifiable anchor; version text is never rewritten, so a scroll position is enough.
- **Input:** only this conversation's covered range is sent. The branch's source quotation and source answer are sent as one separate system note, labelled “只用于理解背景，不要总结” ("only for understanding the background; do not summarize"), not as conversation turns, so that the model does not write ancestor content into the summary. When the budget is exceeded, the S3 planning and context summary are reused, inside the summary task's own execution slot.
- **Output:** limit 16,384 (for the reasons in D-015); the first line is “标题：…” ("Title: …"), and the rest is the body. Only a result that finished normally with a non-empty title and body is accepted; otherwise the task fails and leaves no version.
- **Same-name normalization:** NFKC, trim leading and trailing whitespace and punctuation, collapse whitespace, case-insensitive; synonyms are not merged. The naming request gets each node's current title, the first 600 characters of its body and the existing names, and the result is written as "base name－distinguishing word".

## D-018 The summary network canvas is custom-drawn; keyboard and screen-reader access go through the list view (2026-10-28)

Architecture §11.1 says "background and edges custom-drawn, nodes as lightweight ordinary controls". The first release draws the nodes too: with few nodes the difference is small, but one shared coordinate transform, hit testing and drawing are simpler, and text and edges do not drift apart when zooming. The cost is that nodes on the canvas have no per-node accessibility semantics. For this reason the S4 list view stays (toggled with “列表” ("List") in the toolbar): each summary is an ordinary button that can be used from the keyboard and read aloud, with its markers written as text. On the canvas, too, state does not rely on color alone: unread nodes have a small solid dot, candidate updates add "↻" after the short name, selection adds a dark outline, and the details panel spells everything out in full. If dragging is delivered later, or keyboard navigation inside the canvas is needed, nodes will be replaced with controls.

## D-019 Short names for summary network nodes, and the reference layout for new nodes (2026-10-28)

- **Short names** (the user's feedback after trying S4): each version also stores a short name of 2–8 characters, generated together with the summary (a line “短名：…” ("Short name: …")). Nodes show the current version's short name; the full name is in the details. For older versions, or when this line is missing, the first 8 characters of the title plus "…" are used. Short names take no part in same-name naming. Migration v7.
- **New nodes use the in-memory layout as reference** (MW-C12 requires this to be written down): when the summary network is open and has an unsaved arrangement, a new node is placed next to the source node the user sees, and that row is saved directly. If the user later chooses “不保存” ("Don't save") for that arrangement, the new node's saved position is near the parent's arranged position, so after reopening it may be a little farther from its parent; the position is still valid and can be arranged again. When there are no unsaved changes, the in-memory layout is the saved layout, and there is no difference between the two.
- **Layout parameters:** layer spacing 170, group padding 160, 40 rounds of collision correction, zoom 0.25–2.5 (no more than 1.5 when fitting), fit margin 48. Label widths are estimated by character (CJK characters 14, ASCII 8) rather than measured from the font, so the same input gives the same coordinates on different machines.

## D-020 UI polish is a separate stage after S6 (2026-10-28)

After trying S5, the user pointed out that the summary network page is clumsier than the sample confirmed before development (`prototypes/network-layout`); that read and unread are not visually clear; and that the layout and details of pages such as starting a branch conversation and summaries are not good enough. There were no functional problems. The user decided: after S6 is finished, do one focused UI polish stage (recorded as S7); during S6, do no UI rework, and implement new pages in the existing style, aiming only for usable.

Scope of S7 (each page first gets before and after screenshots; work continues only after the user confirms the look):

- **Redo the summary network page in the sample's visual language:** dark canvas and edge colors, enlarging on hover and brightening adjacent edges, highlighting the source path back to the root on selection, labels beside the dots, hiding secondary labels when zoomed out, rings around search matches. Only the display changes; the tested behavior of layout, saving, viewport and the leave prompt stays the same. Dragging nodes (including neighbor springs) comes only after separate "manual position" rules are set.
- **Read/unread:** unread is a solid bright dot + bold label + accent-color badge; read is a hollow circle with a gray label. The page gets a one-line legend; the summary list and the details panel use the same convention.
- **Branch conversations, the summary page, the conversation page and other S1–S6 pages:** unify spacing, hierarchy and button positions, adjusting page by page against screenshots.

From now on, every stage report states which screens are only a usable first version, and whether they are scheduled for polish.

## D-021 Deep restore restores the whole path; E5 is not in the first release (2026-10-28)

The user confirmed during the S6 design (the interaction review MW-C18 requires):

- **An archived conversation cannot generate a summary directly**; instead it offers “恢复并总结” ("Restore and summarize"). Existing summaries can still be read, edited, adopted, and have versions deleted as usual. Asking, branching and retrying likewise go through “恢复并继续” ("Restore and continue").
- **Restoring a conversation that is hidden because an ancestor was archived restores the whole path**: it also clears the mark on the archived ancestors along the path, and the confirmation dialog first lists those ancestors and the number of other branches that reappear as a result. Separate archives outside the path are not affected. The option "restore only the conversation itself, with ancestors grayed out" was not adopted (its visibility and re-archiving semantics are more complex).
- **The E5 exchange package is not implemented in S6**; it is left until after the first release. Export uses a separate formatter and does not serialize stored records directly, so adding E5 later needs no rework. Requirements §8.3 and architecture §8.1 state that the first release has no project package.

## D-022 Both light and dark, switchable (2026-10-29)

At the start of S7 the user was given three options: make only the summary network page dark like the sample, make the whole app dark, or make everything light. The user chose "both, switchable". Implementation: on the settings page, “外观：跟随系统／浅色／深色” ("Appearance: system / light / dark"), defaulting to following the system and taking effect immediately. In dark mode the whole app uses the sample's colors, and the summary network page matches the sample. In light mode the summary network page keeps the sample's behavior (hover, selected path, search rings, labels on the right, labels hidden when zoomed out), with light colors. All colors are referenced only through light/dark theme resources of the same name; the custom-drawn canvas does not hard-code colors either. Read and unread are distinguished by the same shapes in both themes (solid = unread, hollow = read), not by color alone.

## D-023 Round 2 acceptance feedback: manual node dragging delivered early; less explanatory text in the UI (2026-11-02)

After the round 2 acceptance of S7, the user gave 8 points of feedback and agreed to do them together with round 3 of S7:

1. The left sidebar can be resized and collapsed (its width and collapsed state are remembered).
2. Remove the explanatory text in the appearance settings (“立即生效……” ("Takes effect immediately…"), “跟随系统……” ("Follows the system…")); the three radio buttons become a compact segmented control.
3. Model services offer preset URLs, so not everything has to be typed by hand.
4. After the model list is fetched, the candidates should show directly, without first typing a correct prefix.
5. The stop button after sending shows only an icon, without the text “停止” ("Stop").
6. The button for creating a branch from selected text is fixed at the bottom of the page; it should appear next to the selection instead.
7. Nodes in the summary network can be dragged.
8. Both questions and answers show their time and can be copied with one click.

Two things were settled from this:

- **Manual node dragging is delivered in S7** (requirements §3 originally listed it as after the first release; the user has now scheduled it). The behavior follows the rules already written in the architecture's "Layout" section: the end of a drag produces a layout waiting to be saved, using the same coordinates and save path as “整理布局” ("Arrange layout"), with the same leave prompt; panning, zooming and showing everything produce no pending-save state. No physics effects such as neighbor springs.
- **Less explanatory text in the UI; let the controls guide the user**: remove hint sentences that restate what a control does. Keep only information the user would misunderstand without seeing it (errors, the consequences of irreversible operations, explanations the requirements explicitly demand). Common interactions (stop, copy, collapse) use icons with hover tooltips. Every page is checked against this rule from now on.

## D-024 Round 4 acceptance feedback: deleting a whole workspace, answer timing, sidebar state, branch markers (2026-11-03)

After the S7 acceptance on Windows by the checklist, the user gave 5 points of feedback (2026-11-02) and made choices on three of them:

1. **A workspace (主题) can be deleted, as a whole**: together with all its conversations (including branches and archived ones), summaries and layouts. A backup is made automatically first, and if the backup fails, nothing is deleted; the user must type the workspace name to confirm; the deletion is refused while an answer or summary is being generated or is queued. **This is inconsistent with requirements §6.1 item 1 (see also §8.2, §9.5)** (the first release does not offer deleting a whole workspace); the user must decide whether to correct the requirements on `main`, see "Pending decisions" in TRACK. The backup before deletion takes no part in the rotation of upgrade backups and is kept indefinitely; when the app opens, there is no way to restore from such a backup (a new feature, not done).
2. **Answer timing**: while generating, a live seconds count is shown; when finished, the total time stays next to the timestamp (“用时 14 秒” ("took 14 s")); failed or stopped answers do not show a time. The data comes from the existing `created_at`/`completed_at`, so no migration is needed.
3. **Sidebar conversation state**: a spinner while generating; an answer that finished while another conversation was open shows a “有新回答” ("new answer") dot (kept after a restart, cleared when the conversation is opened); needing attention (failed, interrupted) takes priority over a new answer. When the sidebar is collapsed, the expand button gives the same indication.
4. **In dark mode, the highlight when returning to the source is hard to see**: a defect. The highlight color is taken from the theme the control is actually in, and is reset when the theme changes; in both themes the contrast between text and highlight is at least 4.5:1.
5. **In long conversations, branch entry points get buried**: next to the scroll bar, answers that have branches are marked by position; clicking a mark scrolls to that answer's branch list. Pages without branches do not take up this strip.

## D-025 Personal information: clean up existing files, do not rewrite git history (2026-11-20)

Health check repo-scripts-9 pointed out that the repository contains personal paths, a course directory and an API relay URL, and that the author email on every commit is a personal email. The user decided (2026-11-20): **do not rewrite history**; future commits use a GitHub noreply email instead (set up by the user).

- In existing files, home directory paths become `~`, prototype commands become paths relative to the repository, the course .docx link in architecture §2.1 becomes a text description, and the relay URL in the responses-compat prototype becomes the placeholder `relay.example.com`.
- Reason: the worst case of the author email leaking is that, once public, it is collected by crawlers and receives spam or phishing email, and the GitHub identity can be linked to the accounts tied to that email; the risk is low. Rewriting history would change every commit ID, invalidating the commit IDs cited in the four tags, the documents and the Windows test records; the cost is higher than the benefit.
- When to reconsider: if the repository is to be made public without exposing the old email, create a new public repository from the current files instead of rewriting this repository's history.

## D-026 Bold opens and closes beside CJK punctuation (2026-11-22)

Answers such as `的**“火箭号”**在` showed their asterisks. Under CommonMark's flanking rules, `**` between a letter and punctuation cannot open emphasis, and `**` between punctuation and a letter cannot close it. Models write this often in Chinese. The shared parsing pipeline therefore enables Markdig's own `UseCjkFriendlyEmphasis` (the markdown-cjk-friendly rule), so finished answers, streaming answers and summaries all get it. Text without CJK characters still parses as plain CommonMark; a test compares the two. The answer text is not rewritten. The rendering configuration identity changes to `livemarkdown-2.5.1/cjk-emphasis-1`; `math-3` anchors still restore, except in a block that used to show those asterisks, which fails the layout check and degrades according to MW-C13.

## D-027 Answer typography: looser body text, lighter coloured bold, bold colour as a setting (2026-11-22)

The user found answer text heavy and not clean enough, and compared fonts, spacing and bold side by side in `prototypes/text-spacing` (its README has the screenshots and measurements). Decided (2026-11-21 and 2026-11-22):

- **Fonts stay as they are**: Inter for the interface, PingFang SC first for answers. Other font sets either lost Chinese bold (any list with a Latin font first) or looked no better.
- **Answer body text** (paragraphs, list items, quotes, table cells) has a 24 px line height, 1.7 times its 14 px size; the font's own spacing was 1.4. List numbers and bullets share it. Headings and code keep their own spacing. The gap between answer blocks is 10 px instead of 8.
- **Bold** is lighter and coloured: Medium weight instead of Semibold, in a muted colour, so it stands out by colour and not by weight. In the light theme Semibold black was too heavy; in the dark theme weight alone did not stand out. Saturated colours were tried and rejected as too vivid.
- **The bold colour is a setting**: Settings › Appearance › “加粗颜色” (Bold colour), with Brown (the default), Slate, Sage, Plum and “与正文相同” (Same as text). Each colour has a light and a dark value in the theme resources. The choice applies at once, like the theme, and is stored under the preference key `bold-colour`; a missing or unknown value means Brown. No migration.

This does not change the requirements or the first-release scope.

## D-028 Usage goes with deleted content; no special case for one over-long message (you, 2026-11-16)

When a conversation or workspace is deleted, its usage disappears with it. During compression, a single message longer than the window is not handled separately; long text and files go through the long text / file import design (requirements v0.16 §10.4). Local models with a window of about 8k are not supported. Reconsider if small local models become a target.

## D-029 Inline reasoning stays in the answer; the output limit is a setting (you, 2026-11-21)

Reasoning that a model writes inline as `<think>…</think>` stays in the answer and does not move into “思考过程” (thinking process). The answer output limit is a setting under 高级设置 (Advanced settings), with an icon, a short explanation and a tooltip (requirements v0.23 §9.6).

## D-030 The backup taken before a deletion is not cleaned up automatically (you, 2026-11-21)

Health check storage-7. You can delete backups yourself in 设置 › 数据与存储 › 备份 (Settings › Data and storage › Backups), so automatic clean-up is not needed.

## D-031 English prompts are checked in daily use, not with a paid run (you, 2026-11-22)

Bilingual merge 3 sends English prompts when the interface is English. They are tried in daily use instead of a paid evaluation run. Reconsider if daily use shows worse answers in English.

## D-032 Progress, history and screenshots each have one place (you, 2026-11-22)

- `STATUS.md` holds only what is true now. Each merge adds a dated entry to [docs/changes.md](../changes.md). Decisions go into this log, and facts about the machines go into [workstation.md](workstation.md). STATUS.md had grown to about 2,700 words because finished work never left it.
- Requirements and architecture state no progress (AGENTS.md, "Changing requirements or architecture").
- Prototype screenshots are not kept in git (`prototypes/*/screenshots/` is ignored). Each prototype can redraw them, and the images earlier rounds were judged on stay in history. They had pushed the source package over its 10 MB limit. History is not rewritten (D-025).

## D-033 The version is raised at each finished plan step; non-release builds name their commit (you, 2026-11-22)

- Every build said "Mindweave 0.1.0", and daily builds carried no commit, so you could not tell which build was running. The version is now 0.2.0 and is raised (minor number) at each finished plan step.
- Outside a release, the line under the settings names the build: version, short commit and the commit's date, then the data channel, e.g. "Mindweave 0.2.0 · 5f50734 · 2026-11-22 · 数据通道 claude-auto". A release still shows only "Mindweave 0.2.0".
- Dev builds take the commit and date from git; `scripts/daily.sh` passes them, because it builds from a `git archive` export. A build from the source package has no git and shows only the version.
