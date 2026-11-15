# Branch-from-selection and return-to-source: anchor capture, persistence and relocation end to end — grade B

The anchoring core is sound and I could not break it. I ran two throw-away probes on a scratchpad copy of the tree (nothing in the worktree was built or changed): every text block in nested lists, tables, fenced and indented code, CRLF text, emoji/ZWJ/astral CJK, block quotes, headings, hard breaks, links, entities and identical duplicate paragraphs captured, serialized and restored on a second, freshly built renderer, always to the same block. A wrong-passage highlight is not reachable without a hash collision; the miss path (MW-C13) works and is tested. A real mouse click on the floating button creates the branch, and return-to-source into a superseded attempt highlights correctly.

It is not an A because of one verified silent corruption of the stored quote (the text shown as the branch title and sent to the model), and because the robustness above is not locked in by tests: the anchor round-trip is directly tested only for formulas. The remaining findings are low-severity limits and UX gaps.

**Strengths**

- Restore refuses rather than guesses, and validates everything before touching the UI (SourceAnchors.cs:256-290): whole-text prefix hash, block span, ordinal, layout-text hash, quote at offset, and the exact raw offset when recorded. Highlights are applied only after every segment passes.
- Probe result: 15 Markdown documents covering nested lists, tables with duplicate cells, fenced and indented code, CRLF, emoji/ZWJ/astral characters, block quotes, hard breaks, links, inline HTML, entities and three identical paragraphs all round-tripped through Capture, Serialize, Deserialize and Restore on a second renderer, each to the same block index.
- Stored answers are parsed once in full while streaming answers use the live builder (AnswerView.Show, lines 260-286), and CanBranch requires a non-live, finished item (MessageItemViewModel.cs:117). Anchors are therefore only captured against the deterministic full parse of saved text.
- Streaming selection is handled with care: the chosen text, not offsets, is remembered (`_chosen`), re-applied after each layout, and handed to the stored view (SelectionCarry.cs, AnswerView.cs:214-237, 292-313). StreamingSelectionTests has nine cases, including a closing emphasis shifting the text and an answer ending mid-drag.
- The source message cannot disappear under a branch: branch_source references are ON DELETE RESTRICT (SchemaMigrations.cs:176-186), deletes remove branch_source rows for the whole subtree before messages (SqliteArchiveStore.cs:80-86), and superseded attempts stay on the page. Probe: after regenerating the parent's answer, return-to-source highlighted the passage in the superseded attempt, 80 px below the viewport top.
- Branch creation re-validates in one write transaction (SqliteBranchStore.cs:13-58): source belongs to the parent, is a finished answer with text, parent not archived; frozen ancestor ranges are copied so later growth never leaks into a branch.
- UI-thread cost of the mark and selection code is bounded: every per-scroll and per-layout handler returns early unless a selection is shown or chosen (AnswerView.cs:292-297, 393-398), BranchMarks.Refresh only touches controls whose place changed, and the back stack is capped (Workbench.cs:346).
- The reload path updates message rows in place (MessageListReconciler.cs), so a highlighted source or a selection survives title arrival and branch creation; the highlight is recoloured on theme change and its underline re-measured on re-wrap (SourceHighlightMark.cs:86-91).

**Not examined**

- Whether a real pointer or keyboard selection can end inside a surrogate pair. Setting it programmatically does: the anchor JSON then stores U+FFFD and the branch can never be located again (probe output), but I did not establish that LiveMarkdown's own gestures can produce such a selection.
- Behaviour on Windows, at display scales other than 1, and with real fonts; the probes ran headless on macOS only.
- Anchors after an actual LiveMarkdown, Markdig or RaTeX upgrade; only the current 2.5.1 packages were available. The hard-coded profile name itself is already reported.
- Block HTML and footnote definitions produced no text block in the probe ('<div>...</div>' and '[^1]: ...' were absent from the block list, and the '[^1]' reference vanished from the paragraph). Whether that content is shown by another control or dropped from the answer was not checked.
- Measured UI-thread timings for selection handling on 100k-character answers; the cost assessment is from reading the code, not from a benchmark.
- ConversationMarksService (unread marks) was read in full but not probed; BranchMarksTests, SourceHighlightThemeTests and BranchFlowTests were not read line by line.
- Context assembly beyond the quote prefix (planner, compression) and the reading-position store.
- Probe files are in /private/tmp/claude-501/-Users-meilingluo-Developer-Projects-mindweave-worktrees-claude-auto/741bda69-6233-404f-b4a0-8bc89d0ed5bc/scratchpad/anchor-probe (tests/Mindweave.Desktop.Tests/AnchorProbe.cs, AnchorProbe2.cs; output probe.txt and probe.txt.2).

### gap-branch-anchor-roundtrip-1. Branch quote substitutes the wrong text for a formula when the paragraph has any other '$' or embedded object

- Final: **medium** (confirmed); auditor said medium
- Where: `src/Mindweave.Desktop/Markdown/SourceAnchors.cs:225`; effort M

**Evidence.** Readable() maps the n-th U+FFFC placeholder in the block to the n-th match of a hand-written regex over the raw block (line 84-85: `\$\$(?<tex>.+?)\$\$|\$(?<tex>[^$\n]+?)\$|...`). The regex does not follow Markdig's math rules and knows nothing of code spans, escaped dollars, or other embedded objects. Probe results (scratchpad copy, current packages): '价格从 $5 涨到 $10，而公式 $a+b$ 不变。' quotes as '价格从 $5 涨到 $10，而公式 $5 涨到 $ 不变。'; '变量 `$HOME` 和 `$PATH` ... 公式 $x^2$' quotes the formula as '$HOME` 和 `$'; '花费 \$5 和 \$6，公式 $a$' gives '$5 和 \$'; an inline image before two formulas shifts them by one ('图 $a$ 与公式 $b$ 和 <U+FFFC>'); a task-list item quotes as '<U+FFFC> 待办'. End to end, the child page showed SourceQuote '公式 $5 涨到 $ 不变，', the sidebar title was the same, and the request sent was '关于上文中的这段内容：\n> 公式 $5 涨到 $ 不变，\n\n为什么？'. The only test (MathRenderingTests line 63) uses a paragraph whose only dollars are the formula.

**Failure scenario.** An answer paragraph mentions a price or a shell variable and also contains a formula (finance, economics, shell-plus-math answers). The user selects the sentence with the formula and creates a branch. The stored quote, the branch title, the quote card and the text prefixed to the first question all carry a fragment of unrelated prose instead of the LaTeX, or a raw object-replacement character. Nothing indicates it, the quote is permanent, and the model is asked about text the user did not select. The anchor itself is unaffected, so return-to-source still highlights the right passage.

**Recommendation.** Derive the replacement from the same parse the renderer shows instead of a regex: walk the block's Markdig inlines in order and map each embedded-object inline (MathInline, image, task-list checkbox) to its source slice by span, so the n-th placeholder is matched by construction. Replace non-formula objects with their alt text or drop them; never let U+FFFC reach the quote. Add cases for currency, code spans with '$', escaped dollars, images and task lists.

**Reproduction attempt: confirmed, medium.** I could not refute it. The code is exactly as cited and no guard, design decision or test covers the scenario.

**Mechanism.** `Readable()` takes every match of the hand-written `Formula` regex over the raw source block and substitutes the n-th match for the n-th U+FFFC in the layout text. Nothing ties a match to what the renderer actually treated as a formula. For `价格从 $5 涨到 $10，而公式 $a+b$ 不变。` the regex's first match is `$5 涨到 $`, while the renderer shows only one object, for `$a+b$`. That one placeholder therefore gets `$5 涨到 $`.

**Probe evidence.** The auditor's probe output is on disk and matches the claim line for line: currency, code span with `$`, escaped dollars, image before formulas, and the end-to-end case. The probe copy of `SourceAnchors.cs` is byte-identical to the worktree file, and the probe calls the real `SourceAnchors.Capture` / `QuoteOf` and the real branch flow. I did not re-run it; I traced the regex by hand for the currency case and got the same result.

**Not intended.** D-014 and D-016 state the intent as restoring placeholders in order to the block's LaTeX so the quote sent to the model does not lose formulas. This behaviour contradicts that intent. TRACK.md does not list it as a known finding.

**No downstream guard.** `AnswerView.axaml.cs:112` passes `QuoteOf(anchor)` straight into `BranchSelection`. `BranchContext.cs:42` prefixes it to the first question. `Restore` checks only `Quote` (the displayed text), never `Readable`, so nothing detects the mismatch and return-to-source still works.

**Tests.** The existing quote tests use paragraphs whose only dollars or delimiters are the formula itself.

**Severity.** Medium holds. It silently corrupts stored data and the text sent to the model in a core feature, with no crash and no anchor damage. The trigger is realistic but narrow: one paragraph must hold both a rendered inline formula and another `$`, or an inline image or other embedded object before it.

*Corrections to the finding.* - The task-list case is a separate, smaller defect. With no formula in the block, `Readable()` leaves the placeholder alone, so the raw U+FFFC in the quote comes from embedded objects in general not being handled, not from a wrong substitution.
- "Nothing indicates it" is slightly overstated. The wrong text is visible to the user in the quote card and the branch title; it just is not flagged as wrong.
- I did not verify that the quote is "permanent" (not editable after creation).
- The trigger needs the extra `$` or embedded object in the same source block as the formula, not merely in the same answer.

<details><summary>Reproduction</summary>

Not re-run (read-only audit; no builds in the worktree). Existing probe output read from /private/tmp/claude-501/-Users-meilingluo-Developer-Projects-mindweave-worktrees-claude-auto/741bda69-6233-404f-b4a0-8bc89d0ed5bc/scratchpad/anchor-probe/probe.txt and /private/tmp/claude-501/-Users-meilingluo-Developer-Projects-mindweave-worktrees-claude-auto/741bda69-6233-404f-b4a0-8bc89d0ed5bc/scratchpad/anchor-probe/probe.txt.2; key lines quoted in evidence.

</details>

### gap-branch-anchor-roundtrip-2. Anchor round-trip is tested only for formulas; no fixture guards old anchors or the hand-maintained compatible-profile list

- Final: **low** (confirmed in part); auditor said medium
- Where: `tests/Mindweave.Desktop.Tests/MarkdownRenderingTests.cs:148`; effort M

**Evidence.** SourceAnchors.Capture and Restore are called directly in one test file only (grep over tests/: MarkdownRenderingTests.cs), in two tests, both about display formulas. BranchUiTests drives one plain paragraph of CJK text. No test captures or restores an anchor in a table cell, code block, nested list, CRLF text, emoji text, duplicate-span ordinal, or a multi-block selection without a formula. No stored anchor JSON from any shipped profile exists as a fixture. SourceAnchors.CompatibleProfiles (lines 73-79) lists four profile names by hand; the only assertion on it is that 'math-2' is contained (MarkdownRenderingTests line 211), and the math-1 and default-pipeline claims in the comment are untested. My probe shows all those block kinds work today, so this is missing protection, not a present defect.

**Failure scenario.** LiveMarkdown or Markdig is upgraded and a span or layout-text rule changes for table cells or code blocks (today a cell's span includes its padding spaces and a fenced block's span includes the fences, both library choices). All tests stay green because none anchors those blocks, and after release every existing branch made from such blocks shows the 'cannot locate' notice. Equally, bumping RenderProfile to math-4 without appending math-3 to CompatibleProfiles makes RevealSource (AnswerView.cs:124) refuse every existing anchor in every database with no failing test.

**Recommendation.** Add a corpus test: a fixed set of Markdown documents covering each block kind, with anchor JSON checked in as captured under each shipped profile; assert each restores to the expected block and range under the current build, or is listed as an accepted loss. Add a test that every profile string ever written by a release is in CompatibleProfiles. This is the gate an upgrade of the rendering packages should have to pass.

**Skeptic: partially confirmed, low.** The factual claims hold. I could not find any guard that refutes them.

**What I verified**
- `SourceAnchors.Capture` and `SourceAnchors.Restore` are called directly from tests only in `MarkdownRenderingTests.cs`, in two tests, both about display formulas.
- `CompatibleProfiles` is a hand-written set of four strings. The only test assertion on it is `Assert.Contains("livemarkdown-2.5.1/math-2", ...)` at line 211.
- The `math-1` and `default-pipeline` entries have no test at all. `math-2` is the only old profile with a real cross-profile restore test, which rebuilds the old pipeline in-test.
- No checked-in anchor JSON fixture exists. The only other profile string in tests is the fake `'livemarkdown-9/other'` in `BranchUiTests.cs:175`, which tests the refusal path.
- No end-to-end test selects inside a table cell, code block or nested list, or uses CRLF or emoji text. The end-to-end branch tests pick a block by `LayoutText.Contains(quote)` on plain paragraphs.
- Neither `TRACK.md` nor `docs/design` records this as a known or accepted gap. `s7-ui-polish.md:111` only argues `math-2` compatibility in prose and cites the one test.

**Why medium is overstated**
- **No present defect.** The auditor's own probe says all block kinds work today; this is missing regression protection only.
- **The failure mode is designed degradation.** A refused or unrestorable anchor produces the `SourceNotLocated` notice and still navigates to the source message (MW-C13). `BranchUiTests.cs:165-185` tests exactly that path. Nothing is lost or corrupted.
- **The trigger is a deliberate act.** The LiveMarkdown version is baked into the profile string (`livemarkdown-2.5.1`), so an upgrade forces a conscious profile decision. All three past bumps appended the previous profile.
- **Blast radius is small.** There are no release tags; "every database" today means development and trial databases.

**Corrections to the evidence**
- End-to-end return-to-source coverage is wider than "BranchUiTests drives one plain paragraph". `MathRenderingTests.cs:83`, `GalleryTests.cs:800` and `SourceHighlightThemeTests.cs:271` also run branch then return-to-source through the real Capture/Restore path, including a paragraph with an inline formula. None covers tables, code blocks or lists, so the gap for those block kinds stands.

The recommendation (a per-profile anchor corpus plus a test that every profile ever written is in `CompatibleProfiles`) is sound and cheap. It is a low-severity test-hardening item, worth doing before the first rendering-package upgrade.

*Corrections to the finding.* - Severity should be low, not medium: this is a test-coverage gap with no present defect, the failure mode is the designed MW-C13 notice with no data loss, and the trigger is a deliberate package or profile bump.
- "BranchUiTests drives one plain paragraph" understates end-to-end coverage: `MathRenderingTests.cs:83`, `GalleryTests.cs:800` and `SourceHighlightThemeTests.cs:271` also run branch then return-to-source through the real Capture/Restore path, including an inline-formula paragraph. None covers table, code or list blocks.
- "Every existing anchor in every database" is bounded today to development and trial databases; there are no release tags.
- A `math-2` omission specifically would be caught by line 211. The untested case is forgetting to append the outgoing profile on the next bump, plus the `math-1` and `default-pipeline` entries.

### gap-branch-anchor-roundtrip-3. The 'cannot locate' fallback shows only 30 characters of the passage and the notice never goes away

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Desktop/Views/Conversations/ConversationView.axaml.cs:182`; effort S

**Evidence.** On a miss, RevealSource sets `page.SourceNotice = UiText.SourceNotLocated(source.Quote)` and scrolls to the top of the source answer. SourceNotLocated (UiText.Conversation.cs:77) wraps the quote in Quoted(), which shortens it to ConversationRules.FallbackTitleLength = 30 text elements. SourceNotice is assigned nowhere else in src (grep): it is never cleared and has no dismiss control (ConversationView.axaml:126-131). Architecture line 187 asks the fallback to show the saved quote; text search is deliberately excluded. The notice is also set, still saying '已回到来源回答', when the container is not found and nothing is scrolled (lines 183-188).

**Failure scenario.** After a renderer change invalidates anchors, the user returns to the source of a branch made from a 600-character passage inside a 30,000-character answer. They land at the top of the answer with the first 30 characters of the quote and no other aid, and must find the passage by reading. The warning bar then stays on that page for as long as the page instance lives in the back stack, across reloads and theme changes.

**Recommendation.** Show the full stored quote in the fallback (wrapped, with a line cap and expand), give the notice a close control or clear it on the first scroll or navigation, and word the no-container case differently. Consider offering 'copy quote' so the user can use the app's own find once one exists.

### gap-branch-anchor-roundtrip-4. Selections over about 280 small blocks are refused as 'too much content' although the quote is far below the 4,000-character limit

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Core/Branches/BranchSource.cs:82`; effort S

**Evidence.** CheckSelection rejects when `selection.AnchorJson.Length > MaxAnchorLength` (64 KB). Each selected text block becomes one AnchorSegment (SourceAnchors.cs:175) carrying a 64-hex-character SHA-256 plus nine named fields: about 220 characters of JSON before the quote (measured from the probe's serialized anchor). Table cells are one block each (probe: an 8-cell table gave 8 segments). The serializer uses JsonSerializerDefaults.Web (line 89), which escapes every non-ASCII character as \uXXXX, so each CJK character of quote costs 6 characters, twice when Readable is also present. Only the constant boundary is tested (BranchRulesTests line 35).

**Failure scenario.** The user selects a whole 36-row by 8-column table of short values (288 cells, roughly 1,200 characters of quote) to ask about it. The anchor is about 66 KB and the branch is refused with '选区跨越的内容太多，请缩小范围', while a 3,900-character single paragraph is accepted. The same happens with a list of some 280 short items.

**Recommendation.** Make the anchor compact: store the layout hash once per distinct block or truncate it (a 16-byte prefix is ample for a tamper-free local check), use a relaxed JSON encoder so CJK is not escaped, and omit null fields. Alternatively size the limit from segment count rather than raw length. Add a test with a large table selection.

### gap-branch-anchor-roundtrip-5. Quotes from tables, lists and code lose their structure: one line per cell or item, no row, marker or fence

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Desktop/Markdown/SourceAnchors.cs:187`; effort M

**Evidence.** QuoteOf joins segments with '\n' and nothing else. Probe output: a table selection quotes as '名称\n值\n甲\n1\n乙\n2\n甲\n1'; a nested ordered and unordered list as '一级 A\n二级 a1\n二级 a2\n三级 x\n三级 y\n...' with no numbers or indentation; a fenced csharp block as its bare lines with no fence or language. BranchContext.QuotedQuestion (BranchContext.cs:39-43) then prefixes each line with '> ' and sends it. Formulas were given a reading-order fix (WithFormulaBlocks, Readable); other structures were not.

**Failure scenario.** The user selects two rows of a comparison table and asks 'why is this one larger?'. The model receives eight unlabelled lines with no row or column boundaries and must guess which values belong together; the same flattened text is what the branch card and title show. Selecting steps 3-5 of a numbered procedure drops the numbers the follow-up question refers to.

**Recommendation.** When all segments of a selection lie in one table, list or code block, build the quote from the source slice of the covered rows or items (the spans are already known and validated), or at least join cells of a row with ' | ' and keep list markers. Keep the layout-text Quote in the anchor unchanged, as is done for formulas.

### gap-branch-anchor-roundtrip-6. The branch action gives no feedback while the write is in flight, can navigate after the user has moved on, and is silent when capture fails

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Desktop/Views/Conversations/AnswerView.axaml.cs:427`; effort S

**Evidence.** CreateBranchAsync: `if (CaptureSelection() is { } selection && ...) { HideSelectionActions(); await page.CreateBranchAsync(selection); }`. CaptureSelection returns null on any InvalidDataException from Capture (lines 109-117), in which case the click does nothing and the button stays. On the view-model side (ConversationViewModel.cs:224-232) the method awaits the storage write and then calls `_navigator.Navigate(...)` with no check of `_disposed` or of whether this page is still current; there is no busy state. On a rejection the notice is set but the floating button has already been removed and returns only after a new selection gesture. TRACK.md records multi-second writes on slow disks (Windows sync findings of 2026-11-06).

**Failure scenario.** On a slow disk the user clicks 创建分支; the button disappears and nothing else happens for a second or two. Assuming it failed, they click another conversation in the sidebar; the write then completes and the window jumps to the new branch. Separately, if Capture ever refuses (a block without a valid source span), the button simply does not respond and no reason is shown. I found no block kind that triggers this with the current packages.

**Recommendation.** Keep the card visible in a disabled 'creating' state until the outcome arrives; navigate only if the page is still the current page of its slot, otherwise leave a notice; show a short notice when the selection cannot be captured; and restore the button after a rejection while the selection is still there.

### gap-branch-anchor-roundtrip-7. Return-to-source highlights but does not select, contrary to its tooltip and doc comments; the highlight is never cleared

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Desktop/Markdown/SourceAnchors.cs:292`; effort S

**Evidence.** The Restore summary says it 'selects and highlights' (line 244), AnswerView.RevealSource says 'Selects and highlights a branch's source' (line 120), and the button tooltip is '回到来源回答，并选中这段原文' (UiText.Conversation.cs:30). The code sets Highlights and an adorner only (lines 293-302); SelectionStart and SelectionEnd are never written. ClearHighlight is called only from inside Restore (line 292), so nothing removes the highlight short of rebuilding the view.

**Failure scenario.** The user returns to the source expecting the passage to be selected as the tooltip says: Cmd+C copies nothing and no 创建分支 card appears for a second branch from the same passage; they must drag over the highlighted text again. The tint and underline then stay on that passage for the whole time the page is shown, including while the user selects and branches from other text in the same answer.

**Recommendation.** Decide one behaviour and make text and code agree. Either set the selection on the resolved segments (which also brings up the branch card and makes copy work) or change the tooltip and comments to 'highlight'. Clear or fade the highlight on the next selection gesture in that answer.

### gap-branch-anchor-roundtrip-8. Every question row builds a full AnswerView (Markdown renderer, floating button, window-level focus handler) that is never shown

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Desktop/Views/Conversations/ConversationView.axaml:214`; effort S

**Evidence.** The single message DataTemplate (line 198) contains `<v:AnswerView IsVisible="{Binding IsAnswer}" />` for every message; AnswerView.cs line 162 confirms 'The message template hosts this view for questions too (hidden)'. Each instance constructs a MarkdownRenderer, a Button with icon and panel, a Border, four routed handlers on the renderer, EffectiveViewportChanged, ActualThemeVariantChanged, SizeChanged and LayoutUpdated subscriptions (constructor, lines 47-85), and on attach adds a GotFocus handler to the TopLevel (line 199). The test helper has to filter them out (BranchUiTests line 348 comment).

**Failure scenario.** A conversation with 200 exchanges creates 400 AnswerViews, 200 of them for questions. Each layout pass raises 400 LayoutUpdated and viewport callbacks, and each focus change in the window runs 400 handlers; when a TextBox takes focus each of them walks its renderer's visual tree (OnFocusMoved calls HasSelection, which calls Blocks). This adds to the already reported cost of the non-virtualized list and to page-open time, for controls that can never display anything.

**Recommendation.** Use separate templates for questions and answers (a DataTemplate selector or two typed templates), so only answers get an AnswerView. Register one GotFocus handler on the page rather than one per answer.

### gap-branch-anchor-roundtrip-9. Creating a branch reads every earlier message of the parent, bodies included, inside the write transaction to validate one message

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Infrastructure/Sqlite/SqliteBranchStore.cs:121`; effort S

**Evidence.** ReadSource first selects the source message's seq, then calls `SqliteConversationStore.GetMessages(connection, transaction, parent, (int)seq.Value)[^1]`, which materializes all messages of the parent up to that seq with their blocks, only to run BranchRules.CheckSource and a block-count check on the last one. This runs inside database.WriteAsync on the single writer.

**Failure scenario.** Branching from the latest answer of a conversation with 300 messages and several megabytes of answer text reads and allocates all of it on the writer thread before the insert. On the slow-sync machines TRACK.md describes, this lengthens the window in which the click shows no feedback (see the branch-action finding) and delays any streaming checkpoint queued behind it.

**Recommendation.** Read only the source row and its blocks by message id (state, role, block count and length are all that the checks need).

