# Session network, round 1b and round 2: readable timeline, session details, circle packing — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Act on the owner's first trial of round 1 (2026-11-21). Four changes, all behind the gate `MINDWEAVE_SESSION_NETWORK=1`, except the wheel-zoom default:
- The timeline shows full titles: wider, label-sized columns, opening at a readable zoom on the newest end.
- The details panel handles a session's question properly.
- Mouse-wheel zoom is on by default.
- The spatial view uses the hierarchical, weight-aware circle packing of design §6.1 instead of today's radial arrange.

**Architecture:**
- The timeline keeps its column grid (ruling R7 needs it). A new pure step maps column indices to x positions from the widest label in each column.
- A new pure `SpatialPacking` module implements design §6.1 and replaces `SummaryNetworkLayout.Arrange` in the gated spatial view.
- Positions stay computed on every load and are never saved. The `conversation_layout` migration and the between-arranges placement (§6.2–6.3) are not in this plan.

**Tech Stack:** .NET 10, Avalonia 12, CommunityToolkit.Mvvm, xUnit v3 with Avalonia.Headless.

**Spec:** [session-network.md](session-network.md) (§4.2, §4.3, §5, §6.1, §6.4, §7). Round 1 plan and rulings: [session-network-plan-round1.md](session-network-plan-round1.md).

## Owner feedback this plan answers (2026-11-21, verbatim intent)

- The spatial layout is "completely different from what I told you at the start"; the agreed algorithm "would never produce such an irregular layout". Round 1 still used the old radial arrange.
- The timeline should be bolder. Increasing the spacing between dots would let every title show. "Don't chase showing everything on screen at once; show useful information."
- Titles end in ellipses even at maximum zoom.
- The details panel shows a long run of bold text: "Not handling the questions properly is unacceptable."
- Mouse-wheel zoom should be on by default.

## Global Constraints

- Gate: `MINDWEAVE_SESSION_NETWORK=1`, unchanged. Without it, today's network keeps its behaviour, text and tests. The one exception is the wheel-zoom default (Task 3), which the owner asked for everywhere.
- Gated mode never writes layout, and there is no migration.
- Determinism: same input, same coordinates. Order is `created_at`, then id. No randomness.
- Interface text stays plain Chinese consts for now. The bilingual merge converts them later (ledger: "Prioritize Lang").
- Names keep the `SummaryNetwork` prefix in this area, or a glossary term (`Timeline`). `Network` alone is reserved.
- Labels must never cover another dot or another label at zoom ≥ 1 in either view (spec §4.2).
- Run `scripts/test.sh full` before the branch is merged. Merging waits for the bilingual merges 1 and 2 and for the owner's look.

## Review Focus

1. **A 30-character session title on the timeline** shows whole at zoom 1 and does not push into the next column's dot. Test in Task 1.
2. **A workspace with one session** opens with that dot centred at a readable zoom in both views, not blown up to fill the canvas. Test in Task 1 (timeline) and Task 5 (spatial).
3. **A very long first question** (2,000 characters) in the details panel stays a bounded block and never pushes the actions off-screen. Test in Task 2.
4. **A root with 40 one-question branches** packs into a round cluster with no overlapping footprints. Test in Task 4.
5. **Twelve roots of very different weights** keep the spatial view from looking like a timeline (§6.4 rank correlation). Test in Task 4.

---

### Task 1: Timeline with full titles, label-sized columns and a readable opening

**Files:**
- Modify: `src/Mindweave.Application/SummaryNetwork/SummaryNetworkService.cs` (session short title, ~:162)
- Create: `src/Mindweave.Desktop/SummaryNetwork/TimelineColumns.cs`
- Modify: `src/Mindweave.Desktop/SummaryNetwork/SummaryNetworkSession.cs` (timeline positions)
- Modify: `src/Mindweave.Desktop/Views/Pages/SummaryNetworkCanvas.cs` (remove the timeline label cap; opening viewport), `SummaryNetworkGeometry.cs`, `SummaryNetworkPalette.cs`
- Test: `tests/Mindweave.Infrastructure.Tests/Chat/SessionProjectionTests.cs`, `tests/Mindweave.Desktop.Tests/TimelineColumnsTests.cs`, `tests/Mindweave.Desktop.Tests/SessionNetworkCanvasTests.cs`

**Interfaces:**
- Produces:
  - `public static IReadOnlyDictionary<SummaryNodeId, NodePosition> TimelineColumns.Widen(IReadOnlyDictionary<SummaryNodeId, NodePosition> grid, IReadOnlyDictionary<SummaryNodeId, double> labelWidths, double minColumn)`. The column of a dot is `round(x / TimelineLayout.Step)`. Column `c`'s width is `max(minColumn, max over its dots of (dotRoom + LabelGap + labelWidth + ColumnGap))`. The new x of a dot is the sum of the widths of the columns before it. Y is unchanged.
  - Constants: `dotRoom = DotRadius(max) + SummaryRingGap`, `ColumnGap = 28`, `minColumn = TimelineLayout.Step`.

Rules:
- In the session projection, a dot's short title is its full title, with no 10-character cut. The details panel and the list already show the full first question elsewhere. Remove the `FirstTextElements(text, 10)` cut for session dots only; summary nodes keep their own short title.
- The timeline label cap added in the final fix wave is removed: labels fit their column now. Resting labels on the timeline are never trimmed at zoom ≥ 1.
- **Opening view, timeline:** zoom 1.0, with the newest root at about 80% of the canvas width and the main line vertically centred (spec §5.3). "显示全部" (show all) still fits everything.
- **Bolder:** main line 2.0 px, timeline branch lines 1.5 px, label size at zoom 1 unchanged.
- Lane gap grows from 64 to 72, so that two-line labels never meet.

- [ ] **Step 1: Write failing tests:**
  - `TimelineColumnsTests.Columns_take_the_width_of_their_widest_label`: three columns with label widths 50, 300 and 80 give x = 0, then `max(110, dotRoom+gap+50+28)`, and so on, computed per the rule.
  - `TimelineColumnsTests.Rows_keep_their_y_and_order`.
  - `SessionProjectionTests.A_session_dot_carries_its_whole_title`: a 30-character title is unchanged.
  - `SessionNetworkCanvasTests.Timeline_labels_are_whole_and_never_meet_at_zoom_one` (Review Focus 1): 30-character titles on the 数据挖掘-like shape. At zoom 1, no label contains "…", and no two label rectangles or label–dot pairs intersect.
  - `SessionNetworkCanvasTests.The_timeline_opens_at_zoom_one_on_the_newest_root` (Review Focus 2 for the timeline): one session gives zoom 1 with that dot visible. Several sessions give zoom 1 with the newest root inside the right fifth of the canvas.
- [ ] **Step 2: Run** them. Expected: FAIL.
- [ ] **Step 3: Implement** per the rules.
- [ ] **Step 4: Run** `dotnet test tests/Mindweave.Desktop.Tests --filter "TimelineColumnsTests|SessionNetworkCanvasTests|TimelineLayoutTests|SessionNetwork"` and `dotnet test tests/Mindweave.Infrastructure.Tests --filter SessionProjectionTests`. Expected: PASS.
- [ ] **Step 5: Commit** `Session network: timeline columns sized to full titles, readable opening`.

### Task 2: Details panel for a session; gated wording

**Files:**
- Modify: `src/Mindweave.Desktop/Views/Pages/SummaryNetworkHomeView.axaml` (details block, ~:185-240; hint ~:173; empty state ~:164)
- Modify: `src/Mindweave.Desktop/ViewModels/Pages/SummaryNetworkHomeRecords.cs` (`NodeDetail`), `SummaryNetworkHomeViewModel.cs` (`Detail`)
- Modify: `src/Mindweave.Desktop/Text/UiText.SummaryNetwork.cs`
- Test: `tests/Mindweave.Desktop.Tests/SessionNetworkUiTests.cs`

**Interfaces:**
- Produces:
  - `NodeDetail` gains `string? Question = null`, the session's first question, for gated dots.
  - UiText consts:
    - `DetailQuestion = "问题"`
    - `ShowWholeQuestion = "展开"`
    - `HideWholeQuestion = "收起"`
    - `SessionNetworkHint = "单击查看详情，双击打开会话或总结。"`
    - `SessionNetworkEmpty = "还没有会话。在“新探索”提一个问题就会出现在这里。"`
  - `UiText.SessionStarted(DateTimeOffset at) => "开始于 " + FormatTime(at)`.

Rules, when gated:
- **The heading is the session's title**, at the same size as today, in regular weight. Never the question.
- **Under the heading**, a muted label `问题` and the first question in regular weight, at most 4 lines with trimming. A text button `展开` / `收起` toggles the whole question. It shows only when the question is longer than the 4 lines.
- **A dot with a summary** keeps today's heading (the summary's display name), and the question block appears too.
- **The time line reads `SessionStarted`**, not `NodeCreated` (生成于).
- **The hint and the empty state** use the two new texts.
- **A dot without a summary** gets `回到来源会话` (return to source conversation) as the primary button.

- [ ] **Step 1: Write failing UI tests**, gate on, Light and Dark:
  - `The_details_show_the_title_as_heading_and_the_question_below`: the heading text equals the title, and its FontWeight is not SemiBold or Bold. The question block contains the question.
  - `A_very_long_question_stays_bounded_and_expands` (Review Focus 3): a 2,000-character question renders at most 4 lines until `展开`. The action buttons stay inside the panel's bounds.
  - `Gated_texts_speak_of_sessions`: the hint, the empty state and "开始于".
  - `A_plain_dot_has_a_primary_action`.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement**, keeping the ungated details unchanged.
- [ ] **Step 4: Run** `--filter "SessionNetworkUiTests|SummaryNetworkUiTests"`. Expected: PASS. Capture and look at screenshots of the details for a long question.
- [ ] **Step 5: Commit** `Session network: details show the title and the question properly`.

### Task 3: Wheel zoom on by default

**Files:** `src/Mindweave.Desktop/ViewModels/Pages/SummaryNetworkHomeViewModel.cs:247`; tests that assume the old default.

- [ ] **Step 1:** Write a failing test, `Wheel_zooms_by_default`: a new page has `WheelZooms == true`.
- [ ] **Step 2:** Set the default to true. Fix any existing test that scrolls the canvas expecting a pan: give it `WheelZooms = false` explicitly.
- [ ] **Step 3:** Run `--filter "SummaryNetwork|SessionNetwork"`. Expected: PASS.
- [ ] **Step 4:** Commit `Summary network: wheel zoom is on by default (owner 2026-11-21)`.

### Task 4: `SpatialPacking` (pure), design §6.1

**Files:**
- Create: `src/Mindweave.Desktop/SummaryNetwork/SpatialPacking.cs` (it may split into `SpatialPacking.Discs.cs` / `SpatialPacking.Tension.cs` if one file passes about 400 lines)
- Test: `tests/Mindweave.Desktop.Tests/SpatialPackingTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record PackNode(SummaryNodeId Id, SummaryNodeId? Parent, int Order, double Mass, double DotRadius, double LabelWidth, SummaryNodeId? ThreadNext = null)`.
    - `Order` is the rank by created_at, then id.
    - `Mass` is the weight `w`.
    - `ThreadNext` is the next root on the time thread (roots only).
  - `public static IReadOnlyDictionary<SummaryNodeId, NodePosition> SpatialPacking.Arrange(IReadOnlyList<PackNode> nodes)`. It returns **dot centres**.
  - `public sealed record PackingReport(double EnclosingRadius, double IdealRadius, double CentroidOffset, double RootTimeRankCorrelation, int Overlaps)` and `public static PackingReport SpatialPacking.Measure(IReadOnlyList<PackNode> nodes, IReadOnlyDictionary<SummaryNodeId, NodePosition> dots)`, which implements the §6.4 metrics and is used by tests and the trial shots.

Algorithm (design §6.1, owner's idea; the builder may improve any step that measurably does better on §6.4):
1. **Footprint.** Each dot has a footprint circle that covers the dot and its label, with the label to the right of the dot, as the canvas draws it. The centre is the midpoint of the dot+label box, and the radius is half that box's diagonal plus 6. The dot centre is the footprint centre minus the offset.
2. **Subtree discs, bottom-up, iterative.** For each node, place its footprint at the origin. Then pack its children's discs, in `Order`, around it with front-chain packing (d3 `packSiblings`, after Wang et al. 2006), keeping the parent's footprint fixed as the first circle. The node's disc is the smallest circle enclosing the result (Welzl or an equivalent). Positions are relative to the node's footprint centre.
3. **Top level.** Pack root discs in **descending mass** order (ties by `Order`) with the same front-chain packing. The heaviest root settles nearest the centre. The order is by mass, not time.
4. **Centre of gravity.** For up to 30 iterations, compute the mass-weighted centroid of all dots. Move each top-level disc a fraction (0.3) toward closing the offset between the centroid and the enclosing circle's centre, rejecting any move that causes an overlap. Stop when the offset is below 0.05·R or stops improving. Apply the same inside each subtree disc, one level at a time.
5. **Tension.** Run 40 deterministic relaxation steps on top-level discs: springs along `ThreadNext` pairs (strength 0.02) and between each disc's centre and the enclosing centre (0.01). A move is accepted only if no footprint overlaps. It shortens the time thread without lining roots up.
6. **Determinism.** No randomness, fixed iteration counts, and inputs sorted by `Order`.

- [ ] **Step 1: Write failing tests:**
  - `No_footprints_overlap`: on 20 fixed generated workspaces (1–40 roots, depths 0–6), `Measure(...).Overlaps == 0`.
  - `The_enclosing_circle_is_near_ideal`: `EnclosingRadius / IdealRadius ≤ 1.6` on the same set. Report the worst value in the test output.
  - `The_centre_of_gravity_sits_near_the_middle`: `CentroidOffset ≤ 0.15`.
  - `It_is_not_a_timeline` (Review Focus 5): with ≥ 5 roots of varied mass, root x is not monotone in time, and `RootTimeRankCorrelation < 0.6`.
  - `A_wide_fan_packs_round` (Review Focus 4): a root with 40 one-question children gives 0 overlaps and an aspect ratio ≤ 1.3 for that subtree's bounding box.
  - `Children_wind_clockwise_in_creation_order`: in a root's subtree, the angles of its children around the root increase clockwise in `Order`.
  - `Same_input_same_output`.
  - `Labels_never_cover_dots_or_labels`: checked through the footprints (a label box lies inside its footprint).
  - `[Trait("Category","Performance")] Thousand_dots_within_300_ms`, in the MINDWEAVE_PERF pattern.
- [ ] **Step 2: Run** `dotnet test tests/Mindweave.Desktop.Tests --filter SpatialPackingTests`. Expected: FAIL.
- [ ] **Step 3: Implement** the algorithm.
- [ ] **Step 4: Run** the same filter, and the perf test via MINDWEAVE_PERF=1 in Release. Expected: PASS. Put the `Measure` numbers for the worst case in the report.
- [ ] **Step 5: Commit** `Session network: weight-aware hierarchical circle packing`.

### Task 5: Use the packing in the gated spatial view

**Files:**
- Modify: `src/Mindweave.Desktop/SummaryNetwork/SummaryNetworkSession.cs` (spatial positions when gated)
- Modify: `src/Mindweave.Desktop/Views/Pages/SummaryNetworkCanvas.cs` (spatial opening viewport, if needed)
- Test: `tests/Mindweave.Desktop.Tests/SessionNetworkSessionTests.cs`, `SessionNetworkCanvasTests.cs`

**Interfaces:**
- Consumes: `SpatialPacking.Arrange` and `Measure` (Task 4); `SummaryNetworkLayout.LabelWidth` for label widths; `SummaryNetworkGeometry.DotRadius(weight)`.

Rules:
- In gated Spatial mode, positions come from `SpatialPacking.Arrange`. It is built from projection nodes: Branch edges give the parents, `Weight` gives the mass, and TimeThread edges give `ThreadNext`.
- The ungated path is unchanged.
- **Opening view, spatial:** zoom 1.0, centred on the mass-weighted centroid, when the whole picture does not fit at zoom ≥ 0.8. Otherwise fit all. One session gives that dot centred at zoom 1 (Review Focus 2).
- Recommendation nodes keep their placement beside the basis dot; they are not packed.

- [ ] **Step 1: Write failing tests:**
  - `Gated_spatial_uses_the_packing`: session positions equal `SpatialPacking.Arrange` of the same nodes.
  - `The_spatial_view_opens_readably`, covering the one-session case and a large workspace (zoom 1 centred on the centroid).
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `--filter "SessionNetwork|SummaryNetwork|SpatialPacking|Timeline"`. Expected: PASS.
- [ ] **Step 5: Commit** `Session network: the spatial view uses the circle packing`.

### Task 6: Trial shots, metrics and the full suite

- [ ] **Step 1: Render.** Take a fresh `sqlite3 … ".backup …"` copy of the daily database into `<scratchpad>/round2/data`. Run the `SessionNetworkShots` test with `MINDWEAVE_TRIAL_DATA` and `MINDWEAVE_SCREENSHOT_DIR=<scratchpad>/round2/shots`. Extend that test to also capture each view at its opening viewport, plus "显示全部", and to write `SpatialPacking.Measure` per workspace to `<shots>/metrics.txt`.
- [ ] **Step 2: Look** at every PNG and report per workspace:
  - whether full titles show on the timeline at the opening zoom;
  - whether the spatial view is round, full and centred, with no overlaps;
  - the metrics.
- [ ] **Step 3: Run** `scripts/test.sh full`. Expected: 0 failed.
- [ ] **Step 4: Commit** `Session network: round 2 trial shots and metrics`.
