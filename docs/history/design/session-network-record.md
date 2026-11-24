# Summary network: the record of how it was built

Moved out of [session-network.md](../../design/session-network.md) on 2026-11-24, with its section numbers: §9, §11, §13, §14 and §15. Kept as written.

## 9. Iteration plan

Each round runs on its own task branch from `main`. Every round ends with headless screenshots from a copy of the owner's data (the `SpikeSessionNetworkShots` approach) plus a `daily.sh trial` for the owner.

**Gate until the owner accepts (a record; lifted on 2026-11-23, §16).**
- The new views are switched on by the environment variable `MINDWEAVE_SESSION_NETWORK=1`, in every channel, so `MINDWEAVE_SESSION_NETWORK=1 scripts/daily.sh trial <ref>` shows them on a copy of the owner's data.
- Without the variable, every build, the daily build included, shows today's summary network unchanged: its dashed path edges, captions and saving.
- Gated code may merge into `main` because it changes nothing by default.
- **The `conversation_layout` migration does not merge into `main` before acceptance.** Until then, the gated spatial view computes the full §6.1 layout on each load and saves nothing, as the spike did. So the owner's daily data never gets a table that would drift from `node_layout`.
- After acceptance, one release adds the migration, removes the gate and makes the new views the product.

1. **Round 1 — data and timeline.**
   - The projection of conversations as dots: weights, the time backbone, and summary rings.
   - The timeline view, which needs no storage.
   - The removals listed in §4.1.
2. **Round 2 — spatial packing.**
   - §6.1 as a pure layout module with tests and the §6.4 checks.
   - The new-dot placement rules of §6.2, tested against the layout module.
   - The `conversation_layout` migration, written and tried on a copy, but merged only after acceptance (see the gate above).
3. **Round 3 — links and suggestions.** §8. This round can run in parallel with round 1.
4. **Then** the requirements and architecture proposal (§11) for the owner. After acceptance, the dev-only toggle is removed and the views become the product.

The spike branch is deleted when round 1 starts.

## 11. Requirements and architecture text that must change (separate proposal)

| Where | Change |
| --- | --- |
| Requirements §1 | Positioning: recording questions and the thinking path, with summaries as a layer |
| §4 concepts | Network dot = conversation; time thread; timeline; dot size meaning |
| §4.2, §5.2 | The network shows every conversation, not only summaries; the "via unsummarized conversations" path edges disappear |
| §7.1 | Markers on a conversation dot with a summary |
| §7.3 | Layout rules for both views; size = amount asked; time shown on demand |
| §7.4 | Saving applies to the spatial view only; the timeline is never saved |
| §10.3.2 | Recommendation count based on the number of conversations, same tiers (owner, §12); a basis summary is still needed, so no summary means no recommendation |
| §10.3.3–10.3.4 | Quieter link display; suggestions only across explorations |
| Architecture §11 | Projection, both layouts, `conversation_layout` |
| Architecture §2.3 | Glossary rows: time thread, the spatial view's name, lane |

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
10. **Recommendation tiers:** unchanged numbers, now counted in conversations, and still no recommendation without a basis summary.

## 14. Approaches considered

| Option | What it is | Trade-off | Outcome |
| --- | --- | --- | --- |
| A. Arranged along a winding path | Clusters placed along a time-ordered path | Predictable and stable, but too close to the timeline: time imposes its ordinariness | Rejected by the owner |
| B. Force-directed | Springs and repulsion over everything | Most organic, but never holds still, conflicts with saved positions, and can settle differently | Rejected: anti-goal "a map that never holds still" |
| C. Arranged, with motion (the hierarchical packing of §6) | Deterministic packing plus internal tension and the existing hover and drag motion | Stable and round; risk in label footprints and line crossings, measured by §6.4 | **Chosen** (owner 2026-11-20, refined with the owner's packing idea) |

For the timeline, a git-graph-style lane layout was the one sensible way: it is determined by the data, needs no storage and is tidy by construction.

## 15. Rounds 1 and 2 (merged 2026-11-22)

Built on `task/session-network` with subagent-driven development (plans: [round 1](session-network-plan-round1.md), [round 2](session-network-plan-round2.md)), behind `MINDWEAVE_SESSION_NETWORK=1`. Without the variable, every build shows today's summary network unchanged, except that mouse-wheel zoom is now on by default (the owner asked for it everywhere). No migration; with the switch on, nothing writes layout.

**How to try then:** `MINDWEAVE_SESSION_NETWORK=1 scripts/daily.sh trial` (a build of `main` on a copy of the daily data).

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
- With the switch on, a conversation change reloads every visited workspace's network page; fixed on 2026-11-23 (§16).
- Workspaces with several roots have no work budget for packing.
- The code says "session" where the glossary says conversation (`SessionNetworkGate`, `SessionWeights`); settled on 2026-11-23 (§16).
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
