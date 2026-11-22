# Molecule layout (prototype)

A standalone, throwaway console program that lays out the session network's spatial view as molecules and draws the result as SVG pictures. It is the first step of the direction in [session-network.md §15.4](../../docs/design/session-network.md): the owner judges it from pictures before anything in `src/` changes. It does not touch the app or its data.

## Run

Export each workspace from a **copy** of the data (never the live file), then run:

```sh
D="$TMPDIR/molecule"; mkdir -p "$D/data"
sqlite3 "$HOME/Library/Application Support/Mindweave/claude-auto/mindweave.db" ".backup '$D/copy.db'"
sqlite3 "$D/copy.db" "select id,name from workspace" | while IFS='|' read id name; do
  sqlite3 -json "$D/copy.db" "select c.id, b.parent_id as parent, c.created_at as created,
    coalesce((select s.display_name from summary_node s where s.conversation_id=c.id), c.title, substr(b.quote,1,30), 'New conversation') as title,
    (select count(*) from message m where m.conversation_id=c.id and m.role='user') as q,
    exists(select 1 from summary_node s where s.conversation_id=c.id) as summary
    from conversation c left join branch_source b on b.child_id=c.id where c.workspace_id='$id'" > "$D/data/$(echo $name | tr -d ';/ ').json"
done
cd prototypes/molecule-layout
dotnet run -c Release -- "$D/data" screenshots
open screenshots/index.html
```

An optional third argument is a folder of "now" pictures named after the workspaces (shown side by side), or `-`; after it, `Name=value` pairs override the knobs in `Settings` (for example `CentrePull=1.5`). Five made-up workspaces (`Synthetic.cs`) are always added. `screenshots/` is not kept in git.

## How it lays out

1. **Each exploration is one molecule** (`Molecule.cs`). It starts from the ideal shape: children follow their parent in creation order, at the angles below. Then a fixed number of steps settles it:
   - **Bond angles.** At every dot, its lines spread round it. Each line takes room in proportion to the square root of how many dots lie behind it (at most 8), so equal branches sit at equal angles (two lines straight, three at 120°), and a big branch pushes a single dot aside, like a bulky group in a molecule. Branches turn as whole groups.
   - **Alternating turns.** Each dot turns its heaviest branch the other way from the turn that led to it, so a chain with side branches zigzags gently instead of curling into a ring.
   - **Bonds** have a rest length that grows a little with the branch behind them. Around a busy dot (6 lines or more), bonds lengthen until all the titles fit round it, and its leaves alternate between two lengths.
   - **Gaps.** Dots that are not bonded keep apart; between two sibling branches, the gap grows with the smaller branch, so groups read as groups.
   - **Titles** are whole, wrapped to a column about nine Chinese characters wide. Each is put in one of 16 places round its dot: away from the dot's own lines (its widest free angle), off other dots, titles and lines. Titles push other dots away, and a line through a title pushes the title's dot off the line.
2. **Molecules are packed** (`Packing.cs`) so that things close in time are close in space:
   - The heaviest exploration goes in the middle.
   - The others follow outward from it in time order, later and earlier in turn, so each one's neighbour in time is already placed.
   - Each is tried at 8 turns and searched outward from its neighbour, keeping its gap from everything already placed. The gap grows with its size.
   - Among the places that fit, the score is the time thread's length, plus the distance to the middle, plus a cost for every grid cell where the thread would run over a drawn molecule.

   Shapes are compared as drawn (dots, lines, titles) on an 8-pixel grid, not as circles. A first version packed heaviest-first around the middle; its time threads crossed the whole picture.
3. Deterministic: fixed start, fixed number of steps, no randomness.

The checks under each picture (`Picture.Measure`) are guards: overlaps, crossings, lines through dots or titles. They are not what the layout aims for. "Mean angle error" is the distance from equal angles, which is not zero by design wherever branches differ in size.

## Verified and not verified · 2026-11-22

Run on copies of the owner's four workspaces (5 to 12 dots) and five made-up ones (30 to 169 dots), and checked by eye on the rendered pictures:
- No overlaps or line crossings anywhere.
- No line through a title in the owner's workspaces. The made-up ones still have a few, 7 of them in the 12-line fan.
- 169 dots take about 0.7 s on the development Mac, most of it the packing search. That is too slow for the app as it stands.

Not done:
- No interaction (pan, zoom, select) and no "keep positions when a dot is added".
- Text widths are estimated, not measured with the app's font.
- Not tried on Windows.

- The time thread can still pass over a molecule, mostly where it leaves its own root.

Owner's view, 2026-11-22: the look is fine as it is, perhaps too regular; a messy time thread is not a big problem, but things close in time should be close in space, which led to the time-ordered packing above.

Open question: wrapping titles to two lines is new; round 2 drew one line.
