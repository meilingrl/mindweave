# Summary network: every conversation a dot, and the timeline (design)

Status: approved by the owner 2026-11-20 (with the decisions in §12). Rounds 1 and 2 merged into `main` on 2026-11-22 behind a switch; the record is in [its own file](../history/design/session-network-record.md). The spatial view was then redone as a molecule layout (§6.1, §15.5). The owner's decisions for finishing the network, and the lifting of the switch, are in §16. The file keeps its first name, session-network, which code and documents cite; "session" there meant conversation (D-035). Written from the owner's brainstorming of 2026-11-19 and 11-20 and a throwaway preview (branch `task/session-network-spike`, commit `bd793e3`). Nothing here changes the requirements or the architecture by itself: §11 lists the text that must change, and that went to the owner as its own proposal (AGENTS.md, "Changing requirements or architecture"); the owner confirmed it and it merged on 2026-11-23 (requirements v0.25).

Related: [evolution D8](../evolution.md) (sharing the thinking path), [S5 summary network](../history/design/s5-network.md) (the network before it), [requirements](../requirements.md) §4.2, §7, §10.3; [architecture](../architecture.md) §11.

## 1. Intent

The owner's thesis: **answers are cheap; the questions and the path that produced them are what is worth keeping.** Mindweave's centre moves from "help me summarize and review" to "record how I thought and what I asked". This is a shift of centre, not a reversal: summaries stay, as a layer on top of the questions.

Before 0.6.0 the summary network showed only summaries. It grew only when the user clicked "summarize", which the owner rarely does, so it stayed nearly empty. A workspace with five conversations and no summary (Steins;Gate in the owner's data) showed nothing at all. Whole explorations without summaries were invisible.

The redesign:

- **Every conversation is a dot.** Root conversations and branch conversations alike, archived ones included. A summary is something a dot *has*, not what makes it exist.
- **One backbone, two forms.** The exploration starts (root conversations), in the order they were begun, form the backbone of both views:
  - the **timeline**: the backbone drawn as a straight main line, with branches leaving it like diverging world lines (the owner's reference: the world-line presentation in the anime Steins;Gate);
  - the **spatial view**: today's network redrawn as a round, full, orderly picture, with the backbone as an arrowed dotted **time thread** winding between clusters.
- The two views are deliberately the same data in two forms: logically consistent, visibly different, neither redundant (owner).

## 2. Goals, non-goals, anti-goals

**Goals**

1. Every conversation appears; nothing the user asked is hidden. Messy thinking is allowed. Failed attempts are not separate dots anyway: retries stay inside their conversation.
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
- A map that never holds still: positions jumping whenever a conversation is added.
- Distinctions carried by colour alone (requirements §7.3), and loud colour or redundant text (the owner rejected today's link colour, the "关联" (link) pills and the "探索 · XXX" (exploration · XXX) captions).

## 3. Constraints

- **Determinism (MW-C12):** the same input gives the same coordinates.
- **Stable positions (U3, §7.4):** a new dot moves nothing of other explorations, and places are saved directly without an unsaved mark. This applies to the spatial view (option C, §16; nothing is arranged or saved by hand); the timeline is computed and never saved (§5.4).
- **Size must not suggest mastery (§7.3).** "How much was asked here" is compatible; the legend says it in plain words.
- **Keyboard and screen-reader access** go through the list view (D-018); it lists conversations now.
- **Archive is not deletion (owner).** Archived conversations stay on both views, drawn like the others; the details say "archived". Deleted conversations disappear.
- **Real data:** any migration is tried first on a copy (`scripts/daily.sh trial`) and the owner decides.
- **Data already available:** conversations (`created_at`), `branch_source` (parent, anchor, `created_at`), `branch_context_range` (ancestor chains), messages (user questions), summaries and summary links. Nothing new needs recording for this design.
- **Names** come from the architecture §2.3 glossary. The views and the time thread have rows there.

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

Removed: the "探索 · XXX" group captions, the dashed "via N unsummarized conversations" edges (they cannot occur any more: every conversation is a dot), and most of the legend text. The legend keeps one line per mark, in short words.

### 4.2 Labels

A dot's label is its conversation title (an automatic or edited title), shortened to fit. The rule is the builder's, but a label must never cover another dot. The full first question and the title go in the details panel. Labels show when zoomed in, as today.

### 4.3 Size: how much was asked here

- `q(s)` = the number of questions the user sent in conversation `s`. A retry of the same question counts once.
- `w(s)` = `q(s)` + the sum of `q` over all of `s`'s descendant branches.
- Radius = `rmin + k·√(w − 1)`, clamped to `[rmin, rmax]`. Starting values: `rmin` = 5, `rmax` = 14, and `k` such that `w` = 20 reaches about 11. Area tracks the amount; a long exploration's start never becomes huge.
- Tokens were rejected because they mostly measure answer length, which reflects the model's verbosity, not the user's thinking. "Has a summary" was rejected because it already has its own mark.
- The same `w` is the **mass** used by the spatial layout (§6).

### 4.4 Time

- Hovering or selecting a dot shows its start date and time. The details panel always shows them.
- Timeline: roots on the main line are evenly spaced, and a wider gap opens between two roots started on different calendar days. A faint date label sits above the first root of each new day and hides when zoomed out. Branch dots carry no date label; their dates show on hover.
- Spatial view: a faint date label sits under the title of a root dot whose day differs from the previous root's, and hides when zoomed out.
- Days are in the user's local time zone.

## 5. Timeline view

### 5.1 Layout

- **Main line** (lane 0) holds **only root conversations**, in `created_at` order, left to right. They are a fixed step apart, plus the day gap (§4.4). Root spacing never depends on their branches.
- **x follows structure, not global time.** Branch dots do not enter the time order (owner). A branch made weeks later still leaves from its parent. Its own date shows on hover and in the details, never by its x position.
- **Branches**: depth-first from each root, children in `created_at` order.
  - **A root's children always open new lanes**, so they leave the main line at an angle.
  - Below a root, a branch node's **first child continues the node's lane**. Each further child opens a new lane: it leaves the parent at an angle (a short curve) and then runs parallel to the main line.
  - A root's new lanes go above or below the main line, whichever side has a free lane nearer to the main line; ties go above. A branch's children stay on its side of the main line.
  - A branch dot's x is `parent.x + step`, or more if needed to stay right of the previous dot in its lane.
  - A lane may run on past later roots; that is the intended parallel "world line".
  - A lane is free again to the right of its last dot, so later branches can reuse it.
  - Dots never overlap. A connector may cross another lane between two dots, never through a dot. When those rules conflict with compactness, push x to the right within the lane; never move a root.
- The picture (owner's sketch, redrawn in the conversation of 2026-11-20): roots on one horizontal line; from a root, a branch rises at an angle and continues horizontally; a second child of a branch rises again from that branch.

### 5.2 Interaction

The same canvas behaviours as the spatial view: pan, zoom, select, details, double-click opens the conversation (or its summary if it has one, as in the spike), and search by title. There is no "arrange", no "save layout" and no drag.

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

### 6.2 When something changes

Nothing is arranged, dragged or saved by hand. An unchanged exploration returns to its saved place; a changed one is redrawn there and moves only when it has outgrown its place; a new one goes beside its neighbour in time (§16). Placing a root that was started from a recommendation beside its basis summary was planned here and dropped (owner, 2026-11-24): its summary link shows the relation.

### 6.3 Storage

One saved row per exploration (`exploration_place`, migration v18; §16, D-035). The earlier plan, a row per dot, was never merged. `node_layout` is not read or written; it is dropped in a later migration, once the owner has used a daily build of 0.6.0 or later and is satisfied (2026-11-24).

### 6.4 Guards (automatic, on generated workspaces and a copy of the owner's data)

These are things that must not happen, not what the layout aims for: arrangements are judged by the owner from pictures (2026-11-22). The enclosing-circle, centroid and "not a timeline" checks of the first approach are dropped.

- No two dots (summary rings included) overlap. No title covers another dot or another title.
- No two branch lines cross, and no branch line runs through a dot other than its own two.
- Same input, same coordinates, whatever the input order.
- Adding one conversation moves no dot of another exploration, except in an extremely spread-out workspace (§16).
- Layout budgets: arrange of 1,000 dots within 300 ms, and the timeline for 1,000 dots within 100 ms, on the development Mac in Release.

The final judge is the owner, on screenshots and on `scripts/daily.sh trial` (AGENTS.md, "Human in the loop").

## 7. Shared view rules

- Both views live on the same page, switched by one control. The current view is remembered per workspace while the app runs. The page is called **Mindweave** (owner, §12); the switch's two labels are 空间 (spatial) and 时间线 (timeline).
- Selection, details, search, double-click, "return to conversation", the list view and recommendations work identically in both views.
- Summary-only actions (open summary, link) appear only for dots that have a summary.
- The legend explains: dot size = how much was asked here (not how well it is understood); ring = has a summary; dotted arrow = time; solid line = branch.

## 8. Summary links and link suggestions

- **Style:** a thin neutral line with a subtle dash, distinct from both the branch line and the time thread. There is no pill label on the line. The note shows on hover and in the details. Pointing at a link brings out its ends; selecting a dot with links brings out its partners and dims the rest. The owner's complaints: too conspicuous, redundant text, endpoints hard to see.
- **Suggestions do not repeat branches.** Before, `LinkSuggestionPrompt` gave the model summaries without saying which exploration each belongs to, so parent–child pairs looked like the best matches. Now it does this (built 2026-11-23):
  - give each summary its exploration (root) and its lineage (ancestor conversations);
  - tell the model never to pair a summary with its own ancestor or descendant, and to prefer pairs across explorations;
  - in `Parse`, drop any ancestor–descendant pair.
  Cousins in distant sub-branches of one exploration stay allowed: no branch line shows those, so they can be the most useful links. The "at least 3 summaries" gate stays as it is. The requirements text (§10.3.4) has this rule.
- This piece is independent of the views and can ship first.

## 9. Iteration plan

Moved to [the record of how the network was built](../history/design/session-network-record.md) on 2026-11-24.

## 10. Left to the builder

- Constants: step, lane gap, radii, spring strengths, iteration counts and thresholds (with the starting values above).
- The packing and relaxation algorithms, as long as §6.4 holds.
- How lanes are allocated in detail, and the curve shapes.
- Code structure. Keep layouts as pure computation in `Mindweave.Desktop/SummaryNetwork/` (or a renamed folder once the glossary names it), with the projection in `Mindweave.Application/SummaryNetwork/`.

## 11. Requirements and architecture text that must change (separate proposal)

Moved to [the record of how the network was built](../history/design/session-network-record.md) on 2026-11-24.

## 12. Owner's decisions (2026-11-20)

1. **Names.** The page is called **Mindweave** in the interface (today: 总结网络 (summary network)). The switch labels 空间 (spatial) and 时间线 (timeline) are the writer's call (§13).
2. **`node_layout`** is kept for one release after acceptance, for rollback, then dropped.
3. **Recommendation count** (§10.3.2) is based on the number of conversations. Writer's calls (§13): the same tiers (1–10 → 1, 11–30 → 2, over 30 → 3), and a recommendation still needs a basis summary.

"The rest is fine": the calls in §13 are accepted.

## 13. Calls made while writing (check these)

Moved to [the record of how the network was built](../history/design/session-network-record.md) on 2026-11-24.

## 14. Approaches considered

Moved to [the record of how the network was built](../history/design/session-network-record.md) on 2026-11-24.

## 15. Rounds 1 and 2 (merged 2026-11-22)

Moved to [the record of how the network was built](../history/design/session-network-record.md) on 2026-11-24 (§15.1 to §15.5 are there under the same numbers).

## 16. Finishing the network: owner's decisions (2026-11-23)

The owner chose to finish the network (D-035). The remaining work: round 3 (§8), the reload fix (§15.3), stable positions, the §11 proposal, and lifting the switch.

1. **Positions (option C).** The spatial view saves each exploration's place and turn, not each dot. The molecule inside an exploration is always laid out again (§6.1). A changed exploration redraws in place; the others keep their place. Only an exploration that has outgrown its place moves, to the nearest free place. This replaces the per-dot `conversation_layout` of §6.2–§6.3. It is a migration, tried on a copy of the owner's data before it merges.
2. **No summary-only view.** The old summary network view goes when the switch is lifted. Summaries are kept; only that drawing goes.
3. **"Stable" means no large sudden shifts.** Small local changes are allowed; large movements of what the reader has already seen are avoided. The §11 proposal states this for requirements §7.3 and §7.4.
4. **Names.** "Conversation" everywhere, as in the glossary; the code's "session" names changed when the switch was lifted.

Built on 2026-11-23: round 3 (§8: the quiet link, its note while active, a linked dot bringing out its partners) and the reload fix (a conversation change reloads only its own workspace's network page). Suggestions are never on one line of descent.

Built on 2026-11-23 (task/network-stable): exploration places. `SpatialLayout.Arrange` takes each exploration's saved place (its root conversation's dot, the direction of its oldest branch, its size) and returns the places it used. Unchanged explorations go back first, to the very same coordinates, and stay there (only in an extremely spread-out workspace, where the packing grid gets very coarse, can one still be pushed); a changed one redraws in its place, and only when it has outgrown its place does it move, to the nearest place where it keeps at least 40% of its gap; new explorations go beside their neighbour in time and keep 120 units of room to grow, so later branches usually fit where they are. Measured on generated workspaces growing one branch at a time (1,367 additions): the changed exploration moved in 37% of them, more than 100 units in 3.8%, at most about 600; no other exploration moved; nothing overlapped. The other order (the changed one stays, its neighbours step aside) moved more of what the reader had seen and was dropped. Migration v18 adds `exploration_place` (rows go with their root conversation or workspace). The network session saves new or changed places directly on load, like a new node's place (U3); a failed save is worked out again next time. With places, 1,000 dots in 220 explorations lay out in about 60 ms.

Built on 2026-11-23 (task/network-default): the switch is lifted.

- The network of every conversation is the only one. `SessionNetworkGate` and `MINDWEAVE_SESSION_NETWORK` are gone, with the summary-only projection, its radial layout, its path edges and captions, hollow read dots, arranging, dragging, the save button and the leave prompt.
- The names follow D-035 (`ConversationWeights`, `ProjectAsync`, …). `SummaryNetworkSession` keeps its name: it is the workspace's network in memory for one run, not a conversation.
- `node_layout` stays in storage for one release and is no longer read or written; its rows still go with their summary or workspace (§12.2).
- The number of recommended nodes counts conversations (requirements §10.3.2, v0.25); before this it still counted summaries. A workspace whose conversations are in a higher tier than its summaries were may prepare the missing nodes by itself the next time its network opens, or when a conversation makes it reach a tier while the page is open in the background, when automatic preparation is on (a request to the model).
