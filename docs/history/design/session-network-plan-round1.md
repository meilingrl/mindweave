# Session network, round 1: sessions as dots and the timeline — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Behind the gate `MINDWEAVE_SESSION_NETWORK=1`, the summary network page becomes "Mindweave": every session is a dot sized by how much was asked, summaries are rings, and a switch shows the spatial view (today's radial arrange plus an arrowed time thread) or the new world-line timeline. Without the gate, nothing changes.

**Architecture:**
- The Application layer gains a second projection, `ProjectSessionsAsync`, which returns one node per conversation, branch edges and time-thread edges, plus a weight per node. It reuses today's projection types with new optional fields, so the canvas, details, list view, links and recommendations keep working.
- The Desktop `SummaryNetworkSession` chooses the projection and a layout by the gate and a per-workspace view mode. When gated, positions are computed on every load and never saved.
- A new pure `TimelineLayout` places the timeline. Round 2 will replace the spatial layout with circle packing.

**Tech Stack:** .NET 10, Avalonia 12, CommunityToolkit.Mvvm, SQLite (Microsoft.Data.Sqlite), xUnit v3 with Avalonia.Headless.

**Spec:** [session-network.md](../../design/session-network.md) (approved 2026-11-20). Rounds 2 (spatial packing, `conversation_layout`) and 3 (links and suggestions) get their own plans.

## Global Constraints

- The gate is the environment variable `MINDWEAVE_SESSION_NETWORK` with the value `1`, read once at startup, in every channel. Without it, every behaviour, text and test of today's network stays exactly as it is.
- Gated mode never writes layout. No `PlaceAsync`, no `SaveLayoutAsync`, no unsaved mark, and dragging is refused. `node_layout` is untouched. There is no migration in this round.
- Every non-deleted conversation of the workspace is a dot, archived ones included. Order is always `created_at`, then id (determinism, MW-C12).
- Days are the user's local calendar days (`TimeZoneInfo.Local`).
- Dot radius = `clamp(5 + 1.3765·√(w − 1), 5, 14)`, where `w` = the user questions in the session plus those in all its descendants. A retried answer adds no question.
- Interface text, verbatim:
  - page title and sidebar entry: `Mindweave`
  - view switch: `空间`, `时间线`
  - legend: `圆点大小：问了多少，不代表掌握程度`, `外圈：有总结`, `虚线箭头：时间先后`, `实线：分支`
  - day label: `11月20日`, with the year (`2025年11月20日`) when it is not the current year
  - hover label: `<label> · 11月20日 21:38`
- Names: new types keep the `SummaryNetwork` prefix used in this area. The word `Network` alone is reserved for real network connections (architecture §2.3). `Timeline` is already in the glossary.
- Work on branch `task/session-network`. Run `scripts/test.sh full` before merging. Gated code may merge into `main` because it changes nothing by default (design §9).

## Review Focus

1. **Empty workspace, gate on:** the page shows today's empty state in both views, with no exception. Test in Task 6.
2. **Two conversations with the same `created_at`:** the order falls back to id, so coordinates are identical on every load. Test in Task 3.
3. **A 60-deep branch chain:** the timeline stays one lane and no recursion overflows. Test in Task 3.
4. **A conversation with no title yet** (title still generating): its label falls back to the first question, then to `新探索`. Test in Task 2.
5. **Roots on both sides of local midnight:** they land on different days, with a day gap and two labels. Test in Task 3.

---

### Task 1: Question counts per conversation (store)

**Files:**
- Modify: `src/Mindweave.Application/Storage/ISummaryNetworkStore.cs`
- Modify: `src/Mindweave.Infrastructure/Sqlite/SqliteSummaryNetworkStore.cs`
- Test: `tests/Mindweave.Infrastructure.Tests/Chat/SummaryNetworkProjectionTests.cs`

**Interfaces:**
- Produces: `Task<IReadOnlyDictionary<ConversationId, int>> QuestionCountsAsync(WorkspaceId workspace, CancellationToken cancellationToken)` on `ISummaryNetworkStore`. Conversations without user messages are absent from the result.

- [ ] **Step 1: Write the failing test** `Question_counts_count_user_messages_and_ignore_retries`. Use the file's `ConversationAsync`/`BranchAsync` helpers. Ask a second question in the root, then retry the root's last answer through the harness's retry path.
```csharp
var counts = await h.Services.GetRequiredService<ISummaryNetworkStore>().QuestionCountsAsync(h.Workspace, Ct);
Assert.Equal(2, counts[root]);
Assert.Equal(1, counts[branch]);
```
- [ ] **Step 2: Run** `dotnet test tests/Mindweave.Infrastructure.Tests --filter Question_counts_count_user_messages_and_ignore_retries`. Expected: FAIL to compile, because the method doesn't exist yet.
- [ ] **Step 3: Implement** with one read: `SELECT m.conversation_id, COUNT(*) FROM message m JOIN conversation c ON c.id = m.conversation_id WHERE c.workspace_id = $ws AND m.role = 'user' GROUP BY m.conversation_id;`. User messages are never superseded, because `supersedes_id` is assistant-only (schema check).
- [ ] **Step 4: Run** the same filter. Expected: PASS.
- [ ] **Step 5: Commit** `Session network: question counts per conversation`.

### Task 2: The session projection (Application)

**Files:**
- Modify: `src/Mindweave.Application/SummaryNetwork/SummaryNetworkService.cs`
- Modify: `src/Mindweave.Application/SummaryNetwork/ISummaryNetworkLayout.cs`
- Create: `src/Mindweave.Application/SummaryNetwork/SessionWeights.cs`
- Test: `tests/Mindweave.Infrastructure.Tests/Chat/SessionProjectionTests.cs`

**Interfaces:**
- Consumes: `QuestionCountsAsync` (Task 1); `IConversationStore.ListAsync`, which yields `ConversationSummary` with `Conversation`, `FirstQuestion`, `Parent`, `IsArchived` and `IsHidden`.
- Produces:
  - `SummaryNetworkNode` gains optional trailing parameters: `bool HasSummary = true, int Weight = 1, string? FirstQuestion = null`.
  - `public enum SummaryNetworkEdgeKind { Branch, TimeThread }`.
  - `SummaryNetworkEdge` gains `SummaryNetworkEdgeKind Kind = SummaryNetworkEdgeKind.Branch`.
  - `Task<SummaryNetworkProjection> ProjectSessionsAsync(WorkspaceId workspace, CancellationToken cancellationToken)` on `SummaryNetworkService` and `ISummaryNetworkLayout`.
  - `public static IReadOnlyDictionary<ConversationId, int> SessionWeights.Of(IReadOnlyDictionary<ConversationId, ConversationId?> parents, IReadOnlyDictionary<ConversationId, int> questions)`. With `q'(x) = max(q(x), 1)`, `w(s) = q'(s) + Σ q'(d)` over all descendants `d` of `s`. Iterative, no recursion.

Rules for `ProjectSessionsAsync`:
- **Nodes:** one per listed conversation, ordered by `CreatedAt`, then `Id.Value`.
  - A conversation with a summary keeps its summary node id, markers, short title and display name (exactly as `ProjectAsync` builds them).
  - A conversation without a summary gets id `new SummaryNodeId(conversation.Id.Value)`, `HasSummary: false` and markers `(false, false, false)`. Its short title is the title, else the first question, else `新探索`, cut to 10 characters plus `…`. Its display name is the first question, else the short title.
  - `SourceArchived` = `IsArchived || IsHidden`. `Root` and `Ancestors` come from `AncestorChainsAsync`, as in `ProjectAsync`.
- **Edges:** a `Branch` edge from each conversation's `Parent` to it (`Via` empty), and a `TimeThread` edge between each pair of consecutive roots in node order.
- Inject `IConversationStore` into the service's constructor; DI resolves it. `ProjectAsync` is unchanged.

- [ ] **Step 1: Write the failing tests** in `SessionProjectionTests` (copy the harness helpers from `SummaryNetworkProjectionTests`):
  - `Every_conversation_is_a_node_and_summaries_keep_their_ids`: three roots, one with two branches, and one summarized branch. Assert 6 nodes. The summarized node's `Id` equals its summary node id and `HasSummary` is true. The others have `Id.Value == conversation.Value` and `HasSummary` false.
  - `Branch_edges_link_parents_and_the_time_thread_links_consecutive_roots`: assert `Branch` edges `(root→a)`, `(root→b)` and `TimeThread` edges `(r1→r2)`, `(r2→r3)`, and nothing else.
  - `Weights_add_descendant_questions`: root with 2 questions, its branch with 1, which has a branch with 3. Assert weights 6, 4, 3.
  - `Archived_conversations_stay_and_say_so`: archive one root. Its node is present with `SourceArchived` true.
  - `An_untitled_conversation_is_labelled_by_its_first_question` (Review Focus 4): a conversation whose title is still null has a short title equal to the first 10 characters of its first question, plus `…` when longer.
- [ ] **Step 2: Run** `dotnet test tests/Mindweave.Infrastructure.Tests --filter SessionProjectionTests`. Expected: FAIL to compile.
- [ ] **Step 3: Implement** `SessionWeights.Of` and `ProjectSessionsAsync` by the rules above.
- [ ] **Step 4: Run** the same filter, then `--filter SummaryNetworkProjectionTests` to confirm the old projection is unchanged. Expected: all PASS.
- [ ] **Step 5: Commit** `Session network: one node per conversation, branch and time-thread edges, weights`.

### Task 3: `TimelineLayout` (pure)

**Files:**
- Create: `src/Mindweave.Desktop/SummaryNetwork/TimelineLayout.cs`
- Test: `tests/Mindweave.Desktop.Tests/TimelineLayoutTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record TimelineNode(SummaryNodeId Id, SummaryNodeId? Parent, DateOnly Day, int Order)`. `Order` is the node's rank by `created_at`, then id. `Day` is the local day; only roots' `Day` is used.
  - `public sealed record TimelineArrangement(IReadOnlyDictionary<SummaryNodeId, NodePosition> Positions, IReadOnlyList<SummaryNodeId> DayStarts)`.
  - `public static TimelineArrangement TimelineLayout.Arrange(IReadOnlyList<TimelineNode> nodes)`.
  - Constants: `Step = 110`, `LaneGap = 64`, `DayGap = 56`.

The algorithm (design §5.1), iterative, since chains can be deep:
```
roots in Order: x = i·Step + (day changes before i)·DayGap, y = 0; DayStarts = first root of each Day
laneEnd: map lane k (k ≠ 0; k < 0 above, k > 0 below) → rightmost x used
open(x0): for k = 1, 2, …: if lane −k is free at x0 return −k; if lane +k is free at x0 return +k
          (free = no dot at x ≥ x0 − Step/2, and no dot of a lane between 0 and k sits in (x0 − Step, x0])
explicit stack, roots first, children in Order:
  a root's children: each opens a lane at x = root.x + Step
  a branch node's first child: same lane, x = max(node.x + Step, laneEnd[lane] + Step)
  its other children: open(node.x + Step), x = node.x + Step
  y = lane · LaneGap
```
The second "free" condition keeps a connector from passing through a dot.

- [ ] **Step 1: Write the failing tests:**
  - `Roots_sit_on_the_main_line_in_order_one_step_apart`: three roots on one day have positions `(0,0)`, `(110,0)`, `(220,0)`.
  - `A_new_day_opens_a_gap_and_starts_a_day_label`: roots on day 1, 1, 2 have x = 0, 110, 276 and `DayStarts` = [r1, r3].
  - `Midnight_splits_days` (Review Focus 5): two roots whose `Day` differs by one get the `DayGap` between them.
  - `A_roots_children_leave_the_main_line`: no branch has `Y == 0`. The first branch of a root has `Y == -64`.
  - `A_branchs_first_child_continues_its_lane_and_the_next_opens_another`: child 1 has the parent's Y and X + 110. Child 2 has a different Y.
  - `A_lane_runs_past_later_roots_without_moving_them`: a 5-long chain under root 1 leaves root 2 at x = 110.
  - `Dots_never_overlap_and_connectors_never_pass_through_a_dot`: on 300 random nodes from a fixed seed, no two dots share a Y within `Step` in X. Every connector's vertical span at X in `(parent.X, child.X]` contains no other dot.
  - `Same_input_same_output` (Review Focus 2): two calls, and two roots with equal timestamps ordered by id, give identical dictionaries.
  - `A_deep_chain_stays_in_one_lane` (Review Focus 3): a 60-deep chain gives one Y for all branch dots, with no exception.
  - `[Trait("Category", "Performance")] Thousand_dots_within_100_ms`: build 1,000 nodes; the second call (after warm-up) takes under 100 ms in Release.
- [ ] **Step 2: Run** `dotnet test tests/Mindweave.Desktop.Tests --filter TimelineLayoutTests`. Expected: FAIL to compile.
- [ ] **Step 3: Implement** `TimelineLayout.Arrange` as above.
- [ ] **Step 4: Run** the same filter. Expected: PASS. The performance test runs under `scripts/test.sh full`.
- [ ] **Step 5: Commit** `Session network: world-line timeline layout`.

### Task 4: The gate and the view mode (Desktop session)

**Files:**
- Create: `src/Mindweave.Desktop/SummaryNetwork/SessionNetworkGate.cs`
- Modify: `src/Mindweave.Desktop/SummaryNetwork/SummaryNetworkSession.cs` (session and `SummaryNetworkSessions`)
- Modify: `src/Mindweave.Desktop/CompositionRoot.cs:67`
- Test: `tests/Mindweave.Desktop.Tests/SessionNetworkSessionTests.cs`

**Interfaces:**
- Consumes: `ProjectSessionsAsync` (Task 2); `TimelineLayout.Arrange` (Task 3). Timeline input: one `TimelineNode` per projection node, with `Parent` from its `Branch` edge, `Day` = `DateOnly.FromDateTime(CreatedAt.ToLocalTime().DateTime)` and `Order` = its index in the projection.
- Produces:
  - `public sealed record SessionNetworkGate(bool Enabled) { public static SessionNetworkGate FromEnvironment(); }`, registered as a singleton.
  - `public enum SummaryNetworkViewMode { Spatial, Timeline }`.
  - On `SummaryNetworkSession`:
    - `bool IsSessionNetwork` (the gate);
    - `SummaryNetworkViewMode ViewMode { get; }` (default `Spatial`; remembered per workspace for the life of the process, which is the writer's reading of design §7 for a gated round);
    - `void SetViewMode(SummaryNetworkViewMode mode)`, which recomputes positions from the current projection and raises `LayoutChanged`;
    - `IReadOnlyList<SummaryNodeId> DayStarts { get; }`, taken from `TimelineLayout.Arrange(...).DayStarts` in both modes.
  - Constructors: `SummaryNetworkSessions(ISummaryNetworkLayout network, SessionNetworkGate gate)` and `SummaryNetworkSession(WorkspaceId, ISummaryNetworkLayout, SessionNetworkGate)`.

Gated `LoadAsync`:
1. Calls `ProjectSessionsAsync`.
2. Sets positions to `SummaryNetworkLayout.Arrange(...)` (Spatial) or `TimelineLayout.Arrange(...).Positions` (Timeline).
3. Never calls `GetLayoutAsync`, `PlaceAsync` or `SaveLayoutAsync`, and leaves `Revision` and `SavedRevision` alone.

`Move` and `Arrange` do nothing (`Move` returns false) when gated.

- [ ] **Step 1: Write the failing tests.** Each uses `DesktopHarness.Create(s => s.AddSingleton(new SessionNetworkGate(true)))`, seeds two roots and one branch, and opens the network:
  - `Gated_load_shows_every_session_and_saves_nothing`: `Session.Projection.Nodes.Count == 3`, `Session.IsDirty` is false, and `SummaryNetworkService.GetLayoutAsync` returns 0 rows.
  - `Gated_drag_is_refused`: `Session.Move(id, new NodePosition(1, 1))` returns false.
  - `Switching_the_view_changes_positions_and_keeps_the_projection`: `SetViewMode(Timeline)` raises `LayoutChanged`; both roots then have `Y == 0`; the projection is the same instance.
  - `Without_the_gate_nothing_changes`: the default harness projects summaries only (`IsSessionNetwork` false).
- [ ] **Step 2: Run** `dotnet test tests/Mindweave.Desktop.Tests --filter SessionNetworkSessionTests`. Expected: FAIL to compile.
- [ ] **Step 3: Implement** the gate, the mode and the gated load. Pass the gate through `SummaryNetworkSessions`. Register `SessionNetworkGate.FromEnvironment()` in `CompositionRoot`.
- [ ] **Step 4: Run** the same filter, then `--filter SummaryNetwork` for the existing network tests. Expected: all PASS.
- [ ] **Step 5: Commit** `Session network: gate and view mode; gated layouts are computed, never saved`.

### Task 5: Drawing dots, rings, threads, lanes and day labels (canvas)

**Files:**
- Modify: `src/Mindweave.Desktop/ViewModels/Pages/SummaryNetworkHomeRecords.cs` (`NodeView`, `EdgeView`)
- Modify: `src/Mindweave.Desktop/ViewModels/Pages/SummaryNetworkHomeViewModel.cs:1034-1052` (building views)
- Modify: `src/Mindweave.Desktop/Views/Pages/SummaryNetworkCanvas.cs`, `SummaryNetworkPaint.cs`, `SummaryNetworkGeometry.cs`, `SummaryNetworkPalette.cs`
- Modify: `src/Mindweave.Desktop/Text/UiText.SummaryNetwork.cs`
- Test: `tests/Mindweave.Desktop.Tests/SessionNetworkCanvasTests.cs`

**Interfaces:**
- Consumes: Task 2 node and edge fields; Task 4 `IsSessionNetwork`, `ViewMode`, `DayStarts`.
- Produces:
  - `NodeView` gains `bool HasSummary = true, int Weight = 1, DateTimeOffset CreatedAt = default`.
  - `EdgeView` gains `SummaryNetworkEdgeKind Kind = Branch, bool Curved = false`. `Curved` is true for timeline branch edges whose ends differ in Y.
  - `NodePaint` gains `init` properties `SummaryRing`.
  - `EdgePaint` gains `Arrow` (true for a time thread in Spatial) and `Curved`.
  - `internal static double SummaryNetworkGeometry.DotRadius(int weight)`, per the Global Constraints formula.
  - `UiText.SessionNetworkTitle = "Mindweave"`; `UiText.DayLabel(DateOnly day, DateOnly today)`; `UiText.DotHoverLabel(string label, DateTimeOffset at)`.

Drawing rules when gated:
- **Dots:** radius `DotRadius(Weight)` instead of `IsRoot`. Filled (not hollow) for every dot. `SummaryRing` at `r + 3.5` when `HasSummary`. The unread dot as today.
- **Hovered dot:** its label reads `DotHoverLabel(label, CreatedAt)`.
- **Edges:**
  - Spatial branch: straight, solid, no arrow.
  - Spatial time thread: dotted, in the caption colour, with a 6 px open arrowhead at the target's edge.
  - Timeline time thread: the main line, solid, 1.5 px.
  - Timeline branch with `Curved`: a cubic curve from parent to child with control points `(mid.X, from.Y)` and `(mid.X, to.Y)`.
- **Captions:** replaced by one per `DayStarts` entry (`GroupCaption(DayLabel(...), root, [root])`), hidden when the zoom is at or below `LabelHideScale`.

- [ ] **Step 1: Write the failing tests** using `SummaryNetworkCanvasTests`' set-up, with the gate on:
  - `Dot_size_follows_weight`: `PaintNodes()` radius for weight 1 is 5, for 20 is about 11 (±0.05), and for 500 is 14.
  - `Summaries_wear_a_ring_and_every_dot_is_filled`: `SummaryRing` equals `HasSummary`, and `Hollow` is false for all.
  - `The_time_thread_is_dotted_with_an_arrow_in_space_and_the_main_line_on_the_timeline`: check `PaintEdges()` `Dashed` and `Arrow` per kind and mode.
  - `Captions_are_day_labels`: `Captions` texts equal `["11月19日", "11月20日"]` for roots on two days of the current year.
  - `Hover_shows_the_date`: after hovering a dot created at 21:38 on 11-20, its `Label` ends with `· 11月20日 21:38`.
  - `Without_the_gate_paint_is_unchanged`: the existing `SummaryNetworkCanvasTests` pass untouched (run them).
- [ ] **Step 2: Run** `dotnet test tests/Mindweave.Desktop.Tests --filter SessionNetworkCanvasTests`. Expected: FAIL.
- [ ] **Step 3: Implement** the fields, view building and drawing rules above.
- [ ] **Step 4: Run** `--filter "SessionNetworkCanvasTests|SummaryNetworkCanvasTests"`. Expected: PASS.
- [ ] **Step 5: Commit** `Session network: dots by weight, summary rings, time thread, timeline lanes, day labels`.

### Task 6: The page: title, switch, legend, details, open (view and view model)

**Files:**
- Modify: `src/Mindweave.Desktop/ViewModels/Pages/SummaryNetworkHomeViewModel.cs` (`Open`, `Detail`, new properties and command)
- Modify: `src/Mindweave.Desktop/ViewModels/Pages/SummaryNetworkHomeRecords.cs` (`NodeDetail` gains `bool HasSummary = true`)
- Modify: `src/Mindweave.Desktop/Views/Pages/SummaryNetworkHomeView.axaml`
- Modify: `src/Mindweave.Desktop/ViewModels/ShellViewModel.cs` and `src/Mindweave.Desktop/Views/ShellView.axaml:35-39`
- Modify: `src/Mindweave.Desktop/Text/UiText.SummaryNetwork.cs`
- Test: `tests/Mindweave.Desktop.Tests/SessionNetworkUiTests.cs`

**Interfaces:**
- Consumes: Tasks 4 and 5.
- Produces, on the view model:
  - `string Title` (`Mindweave` when gated, else `SummaryNetworkTitle`);
  - `bool IsSessionNetwork`;
  - `bool IsTimeline`;
  - `[RelayCommand] void SetViewMode(SummaryNetworkViewMode mode)`.
- On `ShellViewModel`: `string SummaryNetworkLabel`.
- UiText: `ViewSpatial = "空间"`, `ViewTimeline = "时间线"`, `LegendDotSize`, `LegendSummaryRing`, `LegendTime`, `LegendBranch`, with the verbatim texts from the Global Constraints.

Behaviour when gated:
- The title and the sidebar entry read `Mindweave`.
- A two-button switch `空间 | 时间线` sits at the start of the toolbar.
- "整理布局" (arrange), "保存布局" (save layout) and the "布局已保存" (layout saved) status are hidden.
- The legend shows the four new lines plus unread and candidate. "虚线：经过未总结的会话" (dashed: via unsummarized sessions) is hidden.
- `Open` on a dot without a summary navigates to `ConversationRoute(conversation)`. The details panel hides "打开总结" (open summary), "关联到…" (link to…) and the reading-state switch when `HasSummary` is false.
- The list view lists every dot. A dot without a summary has empty markers.

- [ ] **Step 1: Write the failing UI tests**, each in Light and Dark (`[AvaloniaTheory]`), gate on, seeded with two roots on the same day, one of them with a branch and a summary:
  - `The_page_is_called_Mindweave_and_switches_views`: check the title text and the sidebar entry text. Click the `时间线` button: `IsTimeline` becomes true and every root's Y is 0. Capture `Screenshots.Capture(window, "session-network-timeline-{theme}")` and the spatial counterpart.
  - `Layout_controls_are_hidden_and_the_legend_explains_the_marks`: the arrange and save buttons are not effectively visible; the four legend texts are present.
  - `A_dot_without_a_summary_opens_its_conversation`: `OpenCommand` lands on a `ConversationViewModel` for that conversation. Summary-only buttons are hidden in its details.
  - `An_empty_workspace_shows_the_empty_state_in_both_views` (Review Focus 1): no conversations, so `IsEmpty` is true in both modes and no exception is logged.
- [ ] **Step 2: Run** `dotnet test tests/Mindweave.Desktop.Tests --filter SessionNetworkUiTests`. Expected: FAIL.
- [ ] **Step 3: Implement** the behaviour above. Bind new visibility to `IsSessionNetwork`; keep today's bindings when it is false.
- [ ] **Step 4: Run** the same filter with `MINDWEAVE_SCREENSHOT_DIR` set to a scratch folder, and look at the four screenshots. Then run `--filter SummaryNetworkUiTests` for the ungated page. Expected: PASS.
- [ ] **Step 5: Commit** `Session network: Mindweave page with spatial and timeline views behind the gate`.

### Task 7: Screenshots from a copy of the owner's data, and wrap-up

**Files:**
- Create: `tests/Mindweave.Desktop.Tests/SessionNetworkShots.cs`, ported from `SpikeSessionNetworkShots` on branch `task/session-network-spike` (commit `bd793e3`) with `[Trait("Category", "Trial")]`. It renders each workspace in both modes, gated through the harness override instead of the environment.

- [ ] **Step 1: Port the shot test.** It returns early unless `MINDWEAVE_TRIAL_DATA` is set.
- [ ] **Step 2: Render.** Copy the daily database with `sqlite3 "$HOME/Library/Application Support/Mindweave/claude-auto/mindweave.db" ".backup '<scratch>/data/mindweave.db'"`. Then run the shot test with `MINDWEAVE_TRIAL_DATA=<scratch>/data MINDWEAVE_SCREENSHOT_DIR=<scratch>/shots`. Expected: two PNGs per workspace. Look at each one: no overlaps, readable labels, and the Steins;Gate workspace is no longer empty.
- [ ] **Step 3: Run the full suite** `scripts/test.sh full`. Expected: all pass, with today's skip counts.
- [ ] **Step 4: Commit** `Session network: trial screenshots from a data copy`. Then remove the spike: `git worktree remove ../mindweave.worktrees/session-network-spike && git branch -D task/session-network-spike`.
- [ ] **Step 5: Hand to the owner** (AGENTS.md, "Human in the loop"):
  - the screenshots;
  - the command `MINDWEAVE_SESSION_NETWORK=1 scripts/daily.sh trial task/session-network`;
  - what was and was not verified (no Windows run, and the spatial layout is still round 1's radial arrange).

  Merging into `main` follows the owner's look at the trial.
