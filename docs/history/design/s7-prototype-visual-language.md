# Mindweave network prototype: visual language

Source: `prototypes/network-layout/Program.cs` and `LocalMotion.cs` on `track/claude-auto` (Avalonia 12.1.4, .NET 10). Every value below is taken from the code. Screenshots were rendered headless with Skia at 1320x840 from a scratch copy (not committed); the prototype itself was not modified.

| File | State |
| --- | --- |
| `01-initial.png` | After `Fit()`. Nothing selected, empty search |
| `02-selected-trail.png` | 热效率 selected and `Pick` invoked. The side panel shows the details and the source trail 火车历史 → 蒸汽机－原理 → 热效率 is highlighted |
| `03-hover-and-search.png` | Pointer over 铁路与城市, search query "快门" (one match). No selection. The side panel still shows the previous pick |
| `04-zoomed-out-0.4.png` | Scale 0.40, no selection or query. Node labels are hidden |

## Theme and window

- App theme: Fluent, `ThemeVariant.Dark`. Font: Inter (`WithInterFont`, `new Typeface("Inter")`). CJK glyphs come from system fallback.
- Window: title `Mindweave · 网络布局交互样例`, 1320x840, minimum 900x580, background `#18191b`.
- Root grid: rows `Auto,*,Auto` (toolbar, body, footer) with an outer margin of 18.
- Body grid: columns `*,260`. The canvas host is on the left and the side panel is 260 wide on the right.

## Canvas background

- Fill `#18191b`, the same as the window, so the canvas has no visible frame. `ClipToBounds=true`.
- Dot grid: dots of radius 0.8 in `#292b30`, spaced every 28 screen px. The grid pans with the offset (`offset % 28`) but its spacing does not scale with zoom.

## Nodes

Each node is a filled circle with a short title to its right.

- **Radius:** root (no parent) 10, child 7. Hover adds 2 (root 12, child 9). Radii are in screen px and do not scale with zoom.
- **Fill colour (first matching rule wins):**
  1. hovered `#e2e8ff`
  2. on the selected node's source trail (the selected node included) `#bdc9f2`
  3. read `#747b88` (dimmer)
  4. unread `#b4bacd`
- **Unread dot:** a filled circle of radius 2.5 in `#a3b8ee`, centred at (x + r + 3, y − r − 3), at the node's upper right. Read nodes have no dot. All sample nodes start unread. A node becomes read only when it is opened (double-click or 打开总结), not on a single click, so the read state does not appear in these screenshots.
- **Halo (hover or selected):** a filled disc of radius r + 11 drawn behind the node. Selected `#26324d`, hover-only `#282e3c`.
- **Ring (selected or search match):** a 2 px stroke of radius r + 5, with no fill. Selected `#c1c6fa`, search match only `#dfb87c` (amber). Selected wins when both apply.
- **Draw order per node:** halo, ring, fill, unread dot, label, then the group caption for roots. Nodes are drawn in list order after all edges, so edges sit under the nodes.

## Labels

- **Position:** the text origin is (x + r + 9, y − 9), to the right of the dot, with the top-left corner 9 px above centre.
- **Size:** `13 * min(1, scale / 0.65)`. That is 13 px at scale 0.65 and above, and it shrinks linearly below.
- **Colour:** `#f0f2ff` when hovered or selected, otherwise `#cbd0da`.
- **Hiding:** a label is drawn only when `scale > 0.45`, or when the node is selected, matches the search, or is hovered. At 0.45 and below, the other labels disappear (screenshot 4).
- **Group caption (roots only):** 11 px, `#858b97`, origin (x − 25, y − 42) above the root. The text is `探索 01 · 交通与工业` for id 0 and `探索 02 · 光与影` otherwise. It is always drawn, even below the hiding threshold.

## Edges

Each edge is a straight line from the parent's displayed position to the child's (including any hover or drag offset). The first matching rule decides the colour and width:

| State | Colour | Width | Dash |
| --- | --- | --- | --- |
| Trail: the child is on the selected node's path to the root | `#aab8f5` | 2.3 | the node's own dash style |
| Near: the hovered node is the child or the parent | `#7f899e` | 1.8 | the node's own dash style |
| Indirect (via an unsummarised session), not active | `#626577` | 1.2 | `DashStyle.Dash` |
| Normal | `#363a42` | 1.2 | solid |

The dash depends only on `Indirect`, so an indirect edge stays dashed even when it is highlighted. In the sample, 电力牵引 (id 6) is the only indirect node.

## Zoom, pan and fit

- Initial scale 0.8, offset (80,100). Zoom is clamped to 0.2–2. The ＋ and − buttons step by ×1.2 around the canvas centre.
- Wheel zoom (⌘/Ctrl, or with the 鼠标滚轮直接缩放 option): factor `exp(clamp(deltaY*0.08, −0.3, 0.3))` around the pointer. A plain wheel pans by delta × 35.
- `Fit` adds padding (40 left, 60 top) around the bounds, which account for title width at 16 px per character + 40 and 50 px below. It fits the canvas minus 90 px, clamps scale to 0.25–1.3 and centres the result. `Fit` runs when the window opens.
- `FocusMatch` (Enter in search) selects the first match and centres it at the current scale.
- Hit target: a rectangle starting at (x − 16, y − 20), title length × 14 + 40 wide and 40 tall. It is tested at both the displayed and the layout position. The cursor becomes a hand over a node.

## Motion (decorative, drawing only)

- Hover attraction is on by default. When the pointer is within 90 screen px, the node moves toward it by `5 * strength * sin(π·d/90)` px (1.5 px at the default strength of 30%). Easing factor 0.18 per 16 ms tick.
- Drag spring: neighbours are pulled toward 0.35 × strength × the drag delta, capped at 48 world units. Velocity is `(v + (target − offset)*0.09) * 0.68`. Neighbours spring back on release, and the dragged node keeps its new position.

## Toolbar (top row)

A horizontal StackPanel with spacing 12 and margin (8,8,8,18), containing:
1. `mindweave  /  探索网络`, 17 px, vertically centred
2. A search TextBox, 190 wide, placeholder `查找知识点…`. It updates the query on each change and runs `FocusMatch` on Enter
3. Buttons `显示全部` (Fit), `新增关联节点` (Add) and `重置样例` (Reset + Fit, and restores the side panel text)

Buttons and controls use the default Fluent dark styling.

## Side panel (right, 260 wide, scrollable)

A StackPanel with spacing 16 and margin (24,20,8,8), from top to bottom:
1. Zoom buttons `−` and `＋` (horizontal, spacing 8). The headless render shows `−` as a hyphen-like glyph
2. CheckBox `鼠标滚轮直接缩放` (off)
3. Strength label, 13 px: `交互强度 · 30%（保守）`. After any slider change it reads `交互强度 · N%`, with `（关闭运动）` added at 0
4. Slider 0–100, value 30
5. CheckBox `悬停轻微吸引` (on)
6. Heading, 22 px: `探索网络` by default, or the selected node's title
7. Info text (wrapping, default size):
   - Default: `单击节点查看来源。\n打开正文才会标记已读。`
   - After a pick: `已读|未读 · 当前采用版本`, a blank line, `来源路径`, then the path joined by ` → `. Indirect nodes also get `\n\n经 1 个未总结会话：能源转型`
8. Button `打开总结` (opens the selected node)
9. Help text, 12 px, `#9299a6`, wrapping. It covers the gestures and notes such as `虚线：经过未总结会话` and the trail and motion behaviour

## Detail (summary) overlay

- Covers the canvas host only (not the side panel or toolbar). Background `#1d1e22`, padding 38.
- A StackPanel with spacing 24: a `← 返回网络` button, the title at 28 px, then the body at 16 px (wrapping): `演示总结 · 已读`, the path, and fixed sample text.
- Opening a node marks it read. Going back hides the overlay and re-picks the node, so the canvas viewport and the selection are kept.

## Footer

`交互样例 · 固定内容 · 无 AI / 数据库 · 关闭不保留状态`, 11 px, `#9299a6`.

## Colour summary

| Role | Hex |
| --- | --- |
| Window / canvas background | `#18191b` |
| Grid dots | `#292b30` |
| Detail overlay background | `#1d1e22` |
| Node unread fill | `#b4bacd` |
| Node read fill | `#747b88` |
| Node trail fill | `#bdc9f2` |
| Node hover fill | `#e2e8ff` |
| Unread dot | `#a3b8ee` |
| Selected halo | `#26324d` |
| Hover halo | `#282e3c` |
| Selected ring | `#c1c6fa` |
| Search-match ring | `#dfb87c` |
| Label normal | `#cbd0da` |
| Label hover/selected | `#f0f2ff` |
| Group caption | `#858b97` |
| Edge normal | `#363a42` |
| Edge indirect (dashed) | `#626577` |
| Edge near hover | `#7f899e` |
| Edge trail | `#aab8f5` |
| Secondary text (help, footer) | `#9299a6` |
