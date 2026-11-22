# Session network and timeline (design)

Status: approved by the owner 2026-11-20 (with the decisions in §12). Rounds 1 and 2 merged into `main` on 2026-11-22 behind the switch `MINDWEAVE_SESSION_NETWORK=1`; the record is in §15. The spatial view was then redone as a molecule layout (§6.1, §15.5). Written from the owner's brainstorming of 2026-11-19 and 11-20 and a throwaway preview (branch `task/session-network-spike`, commit `bd793e3`). Nothing here changes the requirements or the architecture by itself: §11 lists the text that must change, and that goes to the owner as its own proposal (AGENTS.md, "Changing requirements or architecture").

Related: [evolution D8](../evolution.md) (sharing the thinking path), [S5 summary network](../history/design/s5-network.md) (today's network), [requirements](../requirements.md) §4.2, §7, §10.3; [architecture](../architecture.md) §11.

## 1. Intent

The owner's thesis: **answers are cheap; the questions and the path that produced them are what is worth keeping.** Mindweave's centre moves from "help me summarize and review" to "record how I thought and what I asked". This is a shift of centre, not a reversal: summaries stay, as a layer on top of the questions.

Today the summary network shows only summaries. It grows only when the user clicks "summarize", which the owner rarely does, so it stays nearly empty. A workspace with five sessions and no summary (Steins;Gate in the owner's data) shows nothing at all. Whole explorations without summaries are invisible.

The redesign:

- **Every session is a dot.** Root sessions and branch sessions alike, archived ones included. A summary is something a dot *has*, not what makes it exist.
- **One backbone, two forms.** The exploration starts (root sessions), in the order they were begun, form the backbone of both views:
  - the **timeline**: the backbone drawn as a straight main line, with branches leaving it like diverging world lines (the owner's reference: the world-line presentation in the anime Steins;Gate);
  - the **spatial view**: today's network redrawn as a round, full, orderly picture, with the backbone as an arrowed dotted **time thread** winding between clusters.
- The two views are deliberately the same data in two forms: logically consistent, visibly different, neither redundant (owner).

## 2. Goals, non-goals, anti-goals

**Goals**

1. Every session appears; nothing the user asked is hidden. Messy thinking is allowed. Failed attempts are not separate dots anyway: retries stay inside their session.
2. Branches and time are never confused: branch lines are solid with no arrow, and the time thread is dotted with a small arrowhead.
3. Time is always findable (hover, details) but visually quiet: no axis, no tick marks.
4. The spatial view looks round, full and orderly at every level, with no floating islands.
5. Dot size carries one logical meaning (§4.3), and a summary carries another (the ring).
6. Content links become quiet and readable, and AI link suggestions stop repeating branches (§8).

**Non-goals (this design)**

- Sharing a trail (D8 phase A) builds on this but is a separate design.
- A map view (D6), manual curation of what is shown, and changes to branching itself.
- New motion beyond what the canvas already has (hover growth, drag springs); a later round may add more.

**Anti-goals: failures even if the code works**

- The spatial view looking like a second timeline: roots lined up left to right in time order. Time must not impose its ordinariness on the spatial view (owner).
- A view per scenario or a growing set of modes ([evolution D8](../evolution.md), principles; owner 2026-11-20).
- A map that never holds still: positions jumping whenever a session is added.
- Distinctions carried by colour alone (requirements §7.3), and loud colour or redundant text (the owner rejected today's link colour, the "关联" (link) pills and the "探索 · XXX" (exploration · XXX) captions).

## 3. Constraints

- **Determinism (MW-C12):** the same input gives the same coordinates.
- **Stable positions (U3, §7.4):** a new dot is placed without moving existing ones, and its place is saved directly without an unsaved mark. Only an explicit "arrange" re-lays everything, and that is saved only by an explicit save. This applies to the spatial view; the timeline is computed and never saved (§5.4).
- **Size must not suggest mastery (§7.3).** "How much was asked here" is compatible; the legend says it in plain words.
- **Keyboard and screen-reader access** go through the list view (D-018); it lists sessions now.
- **Archive is not deletion (owner).** Archived sessions stay on both views, drawn like the others; the details say "archived". Deleted sessions disappear.
- **Real data:** any migration is tried first on a copy (`scripts/daily.sh trial`) and the owner decides.
- **Data already available:** conversations (`created_at`), `branch_source` (parent, anchor, `created_at`), `branch_context_range` (ancestor chains), messages (user questions), summaries and summary links. Nothing new needs recording for this design.
- **Names** come from the architecture §2.3 glossary. `Timeline` exists there as a D8 idea. New terms (time thread, the spatial view's name) need glossary rows (§11).

## 4. What a dot is

### 4.1 Dots and lines

| Element | Source | Drawn as |
| --- | --- | --- |
| Dot | every non-deleted conversation in the workspace | a filled circle; root dots and branch dots are drawn the same, sized by weight (§4.3) |
| Summary | the conversation's summary node, if any | a thin outer ring; the unread and candidate markers stay as today (§7.1), and the ring is never the only sign (the details say "has a summary") |
| Branch line | `branch_source` parent → child | solid, thin, no arrow |
| Time thread | consecutive root conversations by `created_at` | dotted, fainter than branch lines, with a small arrowhead pointing forward in time; spatial view only (on the timeline, the main line plays this role) |
| Summary link | `summary_link` between two summarized conversations | see §8 |
| Recommendation | as today (§10.3.2), next to its basis summary's dot | unchanged |

Removed: the "探索 · XXX" group captions, the dashed "via N unsummarized sessions" edges (they cannot occur any more: every session is a dot), and most of the legend text. The legend keeps one line per mark, in short words.

### 4.2 Labels

A dot's label is its session title (an automatic or edited title), shortened to fit. The rule is the builder's, but a label must never cover another dot. The full first question and the title go in the details panel. Labels show when zoomed in, as today.

### 4.3 Size: how much was asked here

- `q(s)` = the number of questions the user sent in session `s`. A retry of the same question counts once.
- `w(s)` = `q(s)` + the sum of `q` over all of `s`'s descendant branches.
- Radius = `rmin + k·√(w − 1)`, clamped to `[rmin, rmax]`. Starting values: `rmin` = 5, `rmax` = 14, and `k` such that `w` = 20 reaches about 11. Area tracks the amount; a long exploration's start never becomes huge.
- Tokens were rejected because they mostly measure answer length, which reflects the model's verbosity, not the user's thinking. "Has a summary" was rejected because it already has its own mark.
- The same `w` is the **mass** used by the spatial layout (§6).

### 4.4 Time

- Hovering or selecting a dot shows its start date and time. The details panel always shows them.
- Timeline: roots on the main line are evenly spaced, and a wider gap opens between two roots started on different calendar days. A faint date label sits above the first root of each new day and hides when zoomed out. Branch dots carry no date label; their dates show on hover.
- Spatial view: a faint date label sits beside a root dot whose day differs from the previous root's, and hides when zoomed out.
- Days are in the user's local time zone.

## 5. Timeline view

### 5.1 Layout

- **Main line** (lane 0) holds **only root conversations**, in `created_at` order, left to right. They are a fixed step apart, plus the day gap (§4.4). Root spacing never depends on their branches.
- **x follows structure, not global time.** Branch dots do not enter the time order (owner). A branch made weeks later still leaves from its parent. Its own date shows on hover and in the details, never by its x position.
- **Branches**: depth-first from each root, children in `created_at` order.
  - **A root's children always open new lanes**, so they leave the main line at an angle.
  - Below a root, a branch node's **first child continues the node's lane**. Each further child opens a new lane: it leaves the parent at an angle (a short curve) and then runs parallel to the main line.
  - New lanes go above or below the main line, whichever side has a free lane nearer to the main line. Ties go above.
  - A branch dot's x is `parent.x + step`, or more if needed to stay right of the previous dot in its lane.
  - A lane may run on past later roots; that is the intended parallel "world line".
  - A lane is free again to the right of its last dot, so later branches can reuse it.
  - Lanes never cross and dots never overlap. When those rules conflict with compactness, push x to the right within the lane; never move a root.
- The picture (owner's sketch, redrawn in the conversation of 2026-11-20): roots on one horizontal line; from a root, a branch rises at an angle and continues horizontally; a second child of a branch rises again from that branch.

### 5.2 Interaction

The same canvas behaviours as the spatial view: pan, zoom, select, details, double-click opens the session (or its summary if it has one, as in the spike), and search by title. There is no "arrange", no "save layout" and no drag.

### 5.3 Long histories

The timeline grows to the right. "Show all" fits it into view, and the first view opens at the right end (the newest), at a readable zoom. Performance must stay smooth at 1,000 dots (E6 baseline data).

### 5.4 Nothing saved

Positions are computed on every load from the data alone. There is no layout table and no unsaved state.

## 6. Spatial view

### 6.1 Approach: a molecule layout

Structure first, compactness second (owner, 2026-11-22, §15.4). The first approach, circle packing, is recorded in §15.2; it gave the smallest area but hid the structure.

1. **One exploration is one molecule.** At every dot, its lines spread round it. Each line takes room by the square root of how many dots lie behind it (at most 8), so equal branches sit at equal angles (two lines straight, three at 120°), and a big branch pushes a single dot aside, like a bulky group in a molecule. Children follow their parent in creation order.
2. **No curling.** Each dot turns its heaviest branch the other way from the turn that led to it, so a chain with side branches zigzags gently instead of curling into a ring.
3. **No crossings by construction.** A branch's line is at least long enough for the whole branch (its outline, with room for titles) to fit inside its wedge round the parent, as in a radial tree. A busy dot (6 lines or more) gets lines long enough for all its titles to fit round it, and its single-dot leaves alternate between two lengths.
4. **Groups keep gaps.** Dots that are not bonded keep apart; between two sibling branches the gap grows with the smaller branch.
5. **Titles last.** A title is whole, wrapped to about nine wide characters a line, and put in one of 16 places round its dot: away from the dot's own lines (its widest free angle), never on another dot, off other titles and lines. A root that starts a new day carries its date under its title (§4.4).
6. **Settling.** A fixed number of steps (150, then 30 more with titles fixed) of bond angles, bond lengths and the gaps above, branches turning and moving as wholes.
7. **Packing explorations, close in time close in space.** The heaviest exploration goes in the middle. The others follow outward from it in time order, later and earlier in turn. Each is tried at up to 8 turns and placed as close to its neighbour in time as it fits, keeping a gap (growing with its size) from everything already placed, with a gentle pull to the middle and a cost for a time thread over a drawn molecule. Shapes are compared as drawn, on an 8-pixel grid.
8. **Determinism.** Inputs sorted by creation order then id, fixed step counts, no randomness.

The prototype the owner judged is in `prototypes/molecule-layout/`; the app's version is `SpatialLayout`.

### 6.2 Between arranges

Positions are saved per dot, as today (§7.4, U3):

- **A new branch** is placed in free space next to its parent, using today's placement rule (`SummaryNetworkLayout.PlaceNew`) with footprints instead of label boxes. Its position is saved directly.
- **A new root** is placed just outside the current enclosing circle, next to the previous root (the thread's end), and saved directly.
- **A root started from a recommendation** that has a basis summary (requirements §10.3.5: its place should reflect that summary, owner 2026-11-17) is placed in free space next to the basis summary's dot instead. "Arrange" adds a weak spring between the two in step 5 of §6.1, like the time thread.
- **"Arrange"** runs §6.1 in full; the result is unsaved until the user saves, as today.
- Dragging stays as today.

### 6.3 Storage

Positions are keyed by conversation, not by summary node:

- New table: `conversation_layout` (`conversation_id` primary key referencing `conversation` with cascade delete, `workspace_id`, `x`, `y`, `updated_at`).
- **First load after the migration** counts as the workspace's first appearance (U3): the full §6.1 layout runs and is saved directly. Old summary positions are not carried over. Keeping a few of them would freeze today's row-of-trees picture with new dots stuck around it, which is the look the owner rejected.
- `node_layout` is left untouched. Today's view keeps using it while the new views are behind the gate (§9). It is kept for one release after acceptance, for rollback, and dropped in the release after that (owner, §12).
- This is a migration of real data, so it is tried on a copy before publishing.

### 6.4 Guards (automatic, on generated workspaces and a copy of the owner's data)

These are things that must not happen, not what the layout aims for: arrangements are judged by the owner from pictures (2026-11-22). The enclosing-circle, centroid and "not a timeline" checks of the first approach are dropped.

- No two dots (summary rings included) overlap. No title covers another dot or another title.
- No two branch lines cross, and no branch line runs through a dot other than its own two.
- Same input, same coordinates, whatever the input order.
- Adding one session moves no saved dot (once positions are saved, §6.3).
- Layout budgets: arrange of 1,000 dots within 300 ms, and the timeline for 1,000 dots within 100 ms, on the development Mac in Release.

The final judge is the owner, on screenshots and on `scripts/daily.sh trial` (AGENTS.md, "Human in the loop").

## 7. Shared view rules

- Both views live on the same page, switched by one control. The current view is remembered per workspace. The page is called **Mindweave** (owner, §12); the switch's two labels are 空间 (spatial) and 时间线 (timeline).
- Selection, details, search, double-click, "return to conversation", the list view and recommendations work identically in both views.
- Summary-only actions (open summary, link, reading-state switch) appear only for dots that have a summary.
- Today's legend line about unsummarized dashed paths goes. The legend explains: dot size = how much was asked here (not how well it is understood); ring = has a summary; dotted arrow = time; solid line = branch.

## 8. Summary links and link suggestions

- **Style:** a thin neutral line with a subtle dash, distinct from both the branch line and the time thread. There is no pill label on the line. The note shows on hover and in the details. Selecting a link, or a dot with links, highlights both endpoints and dims the rest. The owner's complaints: too conspicuous, redundant text, endpoints hard to see.
- **Suggestions repeat branches:** the cause is that `LinkSuggestionPrompt` gives the model summaries without saying which exploration each belongs to (`LinkSuggestionPrompt.cs:52`), so parent–child pairs look like the best matches. The fix:
  - give each summary its exploration (root) and its lineage (ancestor sessions);
  - tell the model never to pair a summary with its own ancestor or descendant, and to prefer pairs across explorations;
  - in `Parse`, drop any ancestor–descendant pair.
  Cousins in distant sub-branches of one exploration stay allowed: no branch line shows those, so they can be the most useful links. The "at least 3 summaries" gate stays as it is. The requirements text (§10.3.4) gains this rule (§11).
- This piece is independent of the views and can ship first.

## 9. Iteration plan

Each round runs on its own task branch from `main`. Every round ends with headless screenshots from a copy of the owner's data (the `SpikeSessionNetworkShots` approach) plus a `daily.sh trial` for the owner.

**Gate until the owner accepts.**
- The new views are switched on by the environment variable `MINDWEAVE_SESSION_NETWORK=1`, in every channel, so `MINDWEAVE_SESSION_NETWORK=1 scripts/daily.sh trial <ref>` shows them on a copy of the owner's data.
- Without the variable, every build, the daily build included, shows today's summary network unchanged: its dashed path edges, captions and saving.
- Gated code may merge into `main` because it changes nothing by default.
- **The `conversation_layout` migration does not merge into `main` before acceptance.** Until then, the gated spatial view computes the full §6.1 layout on each load and saves nothing, as the spike did. So the owner's daily data never gets a table that would drift from `node_layout`.
- After acceptance, one release adds the migration, removes the gate and makes the new views the product.

1. **Round 1 — data and timeline.**
   - The projection of sessions as dots: weights, the time backbone, and summary rings.
   - The timeline view, which needs no storage.
   - The removals listed in §4.1.
2. **Round 2 — spatial packing.**
   - §6.1 as a pure layout module with tests and the §6.4 checks.
   - The new-dot placement rules of §6.2, tested against the layout module.
   - The `conversation_layout` migration, written and tried on a copy, but merged only after acceptance (see the gate above).
3. **Round 3 — links and suggestions.** §8. This round can run in parallel with round 1.
4. **Then** the requirements and architecture proposal (§11) for the owner. After acceptance, the dev-only toggle is removed and the views become the product.

The spike branch is deleted when round 1 starts.

## 10. Left to the builder

- Constants: step, lane gap, radii, spring strengths, iteration counts and thresholds (with the starting values above).
- The packing and relaxation algorithms, as long as §6.4 holds.
- How lanes are allocated in detail, and the curve shapes.
- Code structure. Keep layouts as pure computation in `Mindweave.Desktop/SummaryNetwork/` (or a renamed folder once the glossary names it), with the projection in `Mindweave.Application/SummaryNetwork/`.

## 11. Requirements and architecture text that must change (separate proposal)

| Where | Change |
| --- | --- |
| Requirements §1 | Positioning: recording questions and the thinking path, with summaries as a layer |
| §4 concepts | Network dot = session; time thread; timeline; dot size meaning |
| §4.2, §5.2 | The network shows every session, not only summaries; the "via unsummarized sessions" path edges disappear |
| §7.1 | Markers on a session dot with a summary |
| §7.3 | Layout rules for both views; size = amount asked; time shown on demand |
| §7.4 | Saving applies to the spatial view only; the timeline is never saved |
| §10.3.2 | Recommendation count based on the number of sessions, same tiers (owner, §12); a basis summary is still needed, so no summary means no recommendation |
| §10.3.3–10.3.4 | Quieter link display; suggestions only across explorations |
| Architecture §11 | Projection, both layouts, `conversation_layout` |
| Architecture §2.3 | Glossary rows: time thread, the spatial view's name, lane |

## 12. Owner's decisions (2026-11-20)

1. **Names.** The page is called **Mindweave** in the interface (today: 总结网络 (summary network)). The switch labels 空间 (spatial) and 时间线 (timeline) are the writer's call (§13).
2. **`node_layout`** is kept for one release after acceptance, for rollback, then dropped.
3. **Recommendation count** (§10.3.2) is based on the number of sessions. Writer's calls (§13): the same tiers (1–10 → 1, 11–30 → 2, over 30 → 3), and a recommendation still needs a basis summary.

"The rest is fine": the calls in §13 are accepted.

## 13. Calls made while writing (check these)

The writer settled these without the owner; each is easy to change.

1. **Timeline x:** x follows structure, not global time, and only roots sit on the main line (§5.1). This is read from the owner's sketch and "branch nodes don't enter the time ordering".
2. **Gate:** an environment variable in every channel, and no migration in `main` before acceptance (§9).
3. **After migration:** a fresh full layout, not today's saved positions (§6.3).
4. **Link suggestions:** exclude ancestor–descendant pairs only; cousins stay allowed (§8).
5. **Recommendation roots:** placed beside their basis summary (§6.2).
6. **Dot size:** questions asked, not tokens (§4.3).
7. **Layout budgets:** 300 ms for arrange and 100 ms for the timeline, at 1,000 dots (§6.4).
8. **Order inside a cluster:** creation order, clockwise (§6.1).
9. **Switch labels:** 空间 and 时间线; the code names go to the glossary proposal (§11).
10. **Recommendation tiers:** unchanged numbers, now counted in sessions, and still no recommendation without a basis summary.

## 14. Approaches considered

| Option | What it is | Trade-off | Outcome |
| --- | --- | --- | --- |
| A. Arranged along a winding path | Clusters placed along a time-ordered path | Predictable and stable, but too close to the timeline: time imposes its ordinariness | Rejected by the owner |
| B. Force-directed | Springs and repulsion over everything | Most organic, but never holds still, conflicts with saved positions, and can settle differently | Rejected: anti-goal "a map that never holds still" |
| C. Arranged, with motion (the hierarchical packing of §6) | Deterministic packing plus internal tension and the existing hover and drag motion | Stable and round; risk in label footprints and line crossings, measured by §6.4 | **Chosen** (owner 2026-11-20, refined with the owner's packing idea) |

For the timeline, a git-graph-style lane layout was the one sensible way: it is determined by the data, needs no storage and is tidy by construction.

## 15. Rounds 1 and 2 (merged 2026-11-22)

Built on `task/session-network` with subagent-driven development (plans: [round 1](../history/design/session-network-plan-round1.md), [round 2](../history/design/session-network-plan-round2.md)), behind `MINDWEAVE_SESSION_NETWORK=1`. Without the variable, every build shows today's summary network unchanged, except that mouse-wheel zoom is now on by default (the owner asked for it everywhere). No migration; with the switch on, nothing writes layout.

**How to try:** `MINDWEAVE_SESSION_NETWORK=1 scripts/daily.sh trial` (a build of `main` on a copy of the daily data).

**What was built:**

- Every conversation is a dot (unsummarized ones too); dot size from the questions asked in it and its branches (§4.3); a ring marks a summary; the time thread links roots in start order.
- Timeline: whole titles in label-sized columns; sub-branches never cross the main line; a faint date above each new day's first root. It opens fitted if the whole fits at zoom ≥ 0.8, otherwise at zoom 1 with the newest root's whole subtree in view.
- Spatial view: the packing of §15.2, with a date beside each root that starts a new day. It opens fitted if the whole fits at zoom ≥ 0.8, otherwise at zoom 1 on the centre of gravity, and follows reloads until the reader pans, zooms or selects.
- Details: the title as heading and the first question as a bounded block with “Show the whole question / Show less”.
- All new text in English and Chinese; the page keeps its view, zoom and selection across a language switch.

### 15.1 What was verified

- Full test suite on macOS before the merge (counts in STATUS.md of the merge commit).
- Trial screenshots of the owner's four workspaces on a `.backup` copy of the daily data (2026-11-22), both views, both languages, dark theme: 0 overlapping footprints, 0 branch crossings, 0 branch lines through dots; enclosing/ideal 1.26–1.48.
- Reviews: a review per task, five review rounds on the packing, and a final review of the whole branch.
- The owner tried the trial build (62083bf) on macOS on 2026-11-22: "isn't perfect, but it can pass"; not satisfied with how the spatial view arranges nodes (§15.4).

### 15.2 The spatial view as built

`SpatialPacking` (pure, deterministic): every dot's footprint is a circle around its dot and title; sub-branches are packed bottom-up as round clusters, children clockwise in creation order, each cluster turned to face its parent; root clusters are packed heaviest first nearest the centre; the centre of gravity is pulled to the circle's centre; branch lines and the time thread act as springs. A workspace with one root is also laid out in wedges (each sub-branch a share of the turn) from several starting angles, and the arrangement that does best on the §6.4 checks of that time (enclosing circle, centroid, crossings) is kept. Work budgets and a spiral fallback keep any single tree of up to 2,000 dots within about 0.3 s. Rulings R-P1 to R-P12 shaped these steps.

### 15.3 Not verified, and known issues

- Not run on Windows; the performance tests last ran on a loaded machine.
- A time-thread line can pass through a dot, and link lines can cross dates and titles (the checks count branch lines only).
- The spatial view re-packs when a question, title or language changes, so dots can move after the reader has panned.
- With the switch on, a conversation change reloads every visited workspace's network page; fix before the switch is lifted.
- Workspaces with several roots have no work budget for packing.
- The code says "session" where the glossary says conversation (`SessionNetworkGate`, `SessionWeights`); settle it with the §11 proposal.
- §11 (requirements and architecture text) is still to be proposed to the owner; round 3 (quieter links, suggestions only across explorations) is not started.

### 15.4 The owner's verdict on the spatial view, and the next direction (2026-11-22)

The owner's points, as subjective impressions:

1. Counting the title inside the circle works, but it is not an excellent approach.
2. The finished layout shows hardly any regularity or sense of clusters: the area got smaller, but the meaning does not come through.
3. Distance between branches is not considered; the minimal enclosing circle crowds everything into one lump.
4. Lines do not repel each other like chemical bonds. They should spread to the widest angles; instead, paths that could run straight zigzag.

Diagnosis: the algorithm puts smallest area first and structure second; the owner wants structure first and compactness second. The §6.4 metrics of that time did not capture what the owner sees, and choosing arrangements by them optimized the numbers, not the look.

Next direction, a molecule layout:

- At every dot, its lines push apart to the widest angles (two lines run straight, three at 120°), so chains run straight.
- Each sub-branch is a group, and groups keep a gap between them, wider between bigger branches.
- Compactness is a gentle pull toward the centre, heaviest topics nearest it, not a hard smallest circle.
- Titles are placed last, in each dot's widest free angle.
- Deterministic: the same start and a fixed number of steps, computed once per load. It is not a live simulation, so it does not meet §14's objection to option B ("never holds still").

It is built first as a prototype in `prototypes/`, rendered on copies of the owner's workspaces and larger synthetic ones, and judged by the owner from pictures before it replaces `SpatialPacking`. The §6.4 checks stay as guards (no overlaps, no crossings), not as the objective.

### 15.5 The molecule layout (2026-11-22)

The owner judged the prototype (`prototypes/molecule-layout/`) from pictures of copies of their workspaces and larger made-up ones: "fine as it is", perhaps too regular; a messy time thread is not a big problem, but things close in time should be close in space, which led to the time-ordered packing in §6.1. `SpatialLayout` replaced `SpatialPacking` in the app, behind the same switch.

Two changes from the prototype came from the larger test workspaces: a line is long enough for its branch's outline to fit its wedge, which removed every crossing in balanced trees of up to 1,023 dots; and the packing search walks each ring of places once for all turns, which brought 1,000 dots in 220 explorations from 1.6 s to about 0.23 s.

Verified: the §6.4 guards on 25 generated workspaces (1 to 302 dots, 1 to 40 explorations, balanced trees, a comb, a broom) and on a copy of the owner's four workspaces; 1,000 dots in about 0.23 s (220 explorations) and 0.1 s (one binary tree) in Release; the app's spatial view rendered on that copy.

Not verified: Windows; the feel on screen beyond screenshots; long titles in English wrapping on real data.

