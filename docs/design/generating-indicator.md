# Generating indicator

The indicator shown while answers and summaries are being generated. It is a "weaving" motion of three dots, and it replaces the former indeterminate progress bar and the static three dots. The user chose and approved this design on 2026-11-19.

## Purpose and scope

- The motion itself says what is happening now; the text and the timer only add to it. The look fits Mindweave's "weaving" image.
- Not changed: the waiting text, the timer, and when the indicator appears and disappears.
- Not replaced: the progress bars at startup and on small attachment chips. They mean "busy", not "generating".

## A family of motions

Always three dots, in the accent color `AccentBrush` (it follows the light or dark theme), about 44×20 px. When the stage changes, the dots slide from the old path to the new one in about 0.3 seconds, without jumping.

| Stage | Motion (code name) | Description |
| --- | --- | --- |
| Waiting for the answer | braiding `Braid` | The three dots loop along a horizontal figure 8, period 1.4 seconds, each a third of a period apart in phase; the dot in front is slightly larger and more solid |
| Thinking | joining `Joining` | An equilateral triangle turns once every 6 seconds; every 2.4 seconds its lines are drawn one by one, the dot whose turn it is grows, and then the lines fade out |
| Compressing the context | tightening `Tightening` | The braid shrinks to about half size and loops faster |
| Body streaming in | threading `Thread` | A faint line runs through the three dots, and a ripple travels along it every 1.2 seconds |
| Generating a summary | braiding `Braid` | Same as waiting |
| Tool executing (2026-11-20) | reaching `Reaching` | Two dots stay on the left; the third reaches out along a faint line and back, period 1.4 seconds |
| Waiting for approval (2026-11-20) | holding `Holding` | The three dots sit still in a row and breathe slowly together, period 2.4 seconds |

Since 2026-11-20, how each stage's text matches its motion is in section 2 of [Reasoning and run status](reasoning-and-status.md). At the end of the answer there is only one status line (motion, text, timer), replacing the former separate waiting text and “正在生成…” (generating…).

## Approach

Three were considered:

1. **A custom-drawn control** (chosen). It follows the approach of the sidebar's `ActivitySpinner`: it requests animation frames only when it is really visible (it stops when hidden, scrolled out of the viewport, when the window is minimized, or when detached from the visual tree), and in tests it can be fixed at one frame. The two share the "take frames only when visible" part.
2. XAML keyframe animations driving three ellipses: simpler, but they cannot draw the figure 8 or the lines, and cannot stop when not seen.
3. An animated image: blurry at small sizes, and it cannot change color with the theme.

The control `WeavingIndicator` has the property `Motion` (`WeavingMotion`); the answer's stage (`AnswerStage`) decides the motion.

## Left to the implementer, adjustable

Speeds, sizes and the exact look of the tightening motion are values chosen during implementation; the user can adjust them after seeing screenshots.
