# Shared page (prototype)

A standalone, throwaway page that shows what a shared thinking path could look like to a reader who was not there. It goes with the draft [sharing.md](../../docs/ideas/sharing.md): the owner judges the look from it before anything in `src/` is designed. It uses made-up data and does not touch the app or its data.

## Run

```sh
open prototypes/share-page/index.html
```

The dark bar at the top holds the prototype's controls and is not part of a shared page: two examples (one exploration in English, several days of explorations in Chinese), structure only or with content, the page language, and the theme. Narrow the window for the phone layout.

## What it shows

- The path drawn as the app's timeline view draws it (session-network.md §5.1, in small): explorations in start order on a dotted main line, branches leaving at an angle and running alongside; dot size by how much was asked; a ring for a summary.
- Structure only: the picture, the same titles as an outline, and nothing of what was said.
- With content: a chosen conversation below the picture; questions in full; answers folded with "show all"; the sentence a branch left from marked and linked, with the branches also listed under a folded answer; "back to where this left from"; the summary as a closed layer on top; an attachment as its name only; web citations as title and URL.

## What it does not show

- Real Markdown rendering, code highlighting, and formulas beyond two written by hand in MathML.
- The exchange data inside the file, and anything about import.
- The share dialog in the app.
