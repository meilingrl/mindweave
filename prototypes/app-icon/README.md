# Application icon prototype

**Status: the owner chose the drawing on 2026-11-24: a brain drawn as one line, like a strand of yarn (sketch D in `brain.mjs`, files `mindweave-logo.svg` and `mindweave-logo-tile.svg`). Rounds 1 to 4 and sketches A to C were not chosen. Open: the background colour, whether the open state with nodes is used, and putting the icon into the app. Nothing here is used by the app.** Mindweave has no icon of its own: the daily build shows the generic macOS one ([daily-build.md](../../docs/design/daily-build.md)).

## How to look

```bash
dotnet run prototypes/app-icon/draw.cs
```

It writes `screenshots/` (not committed): `overview.png`, `dock.png` (light and dark Dock), `small-sizes.png` (32 and 16 px enlarged), `tiles.png` (every candidate on every background), `sheet-N.png` (one candidate at 1024, 128, 32 and 16), and an `.iconset` folder per candidate for `iconutil`.

## Candidates

| | Idea | Default background |
| --- | --- | --- |
| 1 Braid | One root, three strands woven through each other, three branches | Blue |
| 2 Molecule | Nodes and 120° bonds like the summary network; the shape is an m | Ink |
| 3 Linked bubbles | Two speech bubbles woven through each other | Violet |
| 4 Thread | One thread woven over and under the messages of a conversation | Paper |
| 5 Woven M | Two strands make an M and end in one summary | Blue |

Shape and background are separate choices. The amber dot is the one warm accent. Like the interface icons (`prototypes/icon-style/README.md`), all strokes have round caps and joins; the blue starts from the accent `#4B6BFB`.

## Second round

The owner found 3 and 4 acceptable: 3's meaning was not clear enough, 4 was too complex, and the drawing should be flatter and simpler. Files start with `round2-`; solid backgrounds, no gradient and no shadow.

| | Idea | Default background |
| --- | --- | --- |
| 3A Linked bubbles | Candidate 3 with real speech tails | Violet |
| 3B Meeting bubbles | Two conversations; the shared part is amber | Blue |
| 3C One-colour bubbles | The same; the shared part is cut out | Blue |
| 3D Branching bubble | A conversation that branches | Blue |
| 4A Message and branch | A new line grows out of a message | Blue |
| 4B Three lines | Candidate 4 with one smooth thread | Paper |

## Third round

The owner missed beauty, a clean and symmetric feeling, in rounds 1 and 2. Files start with `round3-`; one geometric figure each, one hue and at most one tint of it.

| | Idea |
| --- | --- |
| 6 Knot, 6B in one tone | Two equal loops at right angles, woven |
| 7 Three rings | Three equal rings; no two are linked, the three hold together |
| 8 Junction | One point and three equal branches at 120° |
| 9 Arcade | Two equal arches crossing, four legs at equal distances: an m |
| 10 Basket | Eight equal bars, like lines of text, with one gap everywhere |

## Fourth round

On the third round the owner wrote “not good,so easy now”, read here as too plain. Files start with `round4-`; symmetric figures with gradient, shadow and finer detail.

| | Idea |
| --- | --- |
| 12 Trefoil | One ribbon woven into three equal loops (the trefoil knot) |
| 13 Lattice | Eight equal circles through one centre |
| 14 Constellation | The summary network as a six-fold figure |
| 15 Strands | One question, woven branches, one summary |

## Brain in two states

The owner's idea: the brain is the shape of both icons and the two overlap a lot. `node prototypes/app-icon/brain.mjs` writes `screenshots/brain.html` with three sketches (A even, B messy, C curls), each as one stroke, as nodes, and laid over each other. The nodes sit on the line: where it crosses itself, and along long pieces.

## The chosen drawing

The owner sent a picture of a brain drawn as one line and chose it (2026-11-24). Sketch D is that picture traced by hand: 121 points, one smooth line through them. Where the picture came from is not recorded here; the owner checks that it may be used before a release.

```bash
node prototypes/app-icon/brain.mjs && prototypes/app-icon/chosen-icon.sh
```

`brain.mjs` writes the two logo files next to itself (committed) and one tile per icon size into `screenshots/`. `chosen-icon.sh` turns those into `screenshots/chosen.iconset` and `chosen.icns` (macOS only: it uses `qlmanage`, `sips` and `iconutil`).

| File | What |
| --- | --- |
| `mindweave-logo.svg` | The mark alone, ink on nothing |
| `mindweave-logo-tile.svg` | The mark in yarn white on an ink tile |

The full drawing is used from 128 px. At 32 px it is the outline, the stem and one inner fold with a bolder line; at 16 px only the outline and the stem. The small ones are two strokes, not one line.

## Rules used

- Drawn from geometry at every size, not scaled down: full from 100 px, bolder at 24–99 px, simplest below 24 px (edges on whole pixels).
- 1024 canvas, 824 body, the macOS icon grid.
- The drawings of rounds 1 to 4 and sketches A to C are original: made for this prototype, no icon set or logo used. Sketch D is traced from the owner's picture (see above).

## Checked

2026-11-24, macOS 27.2: an `.icns` of each candidate in a throwaway test bundle, drawn by `NSWorkspace`. The system accepts the shape (no grey frame) and adds its own edge. Not checked: Windows, and a real Dock.

2026-11-24, macOS 27.2, the chosen drawing: `chosen.icns` builds; looked at 256, 128, 32 and 16 px on one sheet. 256 and 128 are clear, 32 reads as a brain, 16 is an outline with a stem. Not checked: a test bundle, the Dock, a light tile, Windows.
