# Font comparison

Status: a standalone, throwaway Avalonia prototype for choosing the interface and answer fonts by eye. It is not product code and touches no data.

## Run

```sh
cd prototypes/font-compare
dotnet run -c Release
```

Avalonia 12.1.4 (the same version as the app), SDK 10.0.402. It uses only fonts already installed on this Mac: Inter (bundled with Avalonia), PingFang SC, Hiragino Sans GB, Noto Sans CJK SC, Noto Serif CJK SC and Charter.

## What it shows

Two (or four) panes side by side, each a miniature of the Mindweave window: the sidebar, a question bubble, an answer with headings, inline bold, a list, numbers and units, a long Chinese paragraph, and the composer. The colours and sizes come from the real app (`Themes/Colors.axaml`, the sidebar and conversation styles). Each pane picks a preset; the bar on top changes all panes at once: rendering (grayscale or subpixel), heading weight, body weight, body size, and the theme.

Under each pane's preset menu, a line names the font that really draws Chinese and Latin text after fallback.

| Preset | Interface | Answers |
| --- | --- | --- |
| A · Now | Fluent window font (Inter), Chinese from the system fallback (PingFang on macOS) | PingFang SC first (the app's current list) |
| B · PingFang SC | PingFang SC | PingFang SC |
| C · Inter + PingFang | `Inter, PingFang SC` | same |
| D · Noto Sans SC | Noto Sans CJK SC (= Source Han Sans) | same |
| E · Inter + Noto Sans SC | `Inter, Noto Sans CJK SC` | same |
| F · Serif answers | PingFang SC | `Charter, Noto Serif CJK SC` |
| G · Hiragino Sans GB | Hiragino Sans GB | same |

Screenshots (rendered to a bitmap at 2x, so the subpixel option is not visible in them): `screenshots/`. `--shot <file.png>` writes one and exits; `--four`, `--dark`, `--presets 0,3`, `--rendering subpixel`, `--heading medium`, `--body 350`, `--size 15` set the starting state. `--diag` prints which font draws each kind of text.

## Findings · 2026-11-21

- The app's interface font is Inter for Latin text only. It comes from the Fluent theme's window font (`ContentControlThemeFontFamily`), not from `WithInterFont()`, which only registers the font; the global default family is still Helvetica. Chinese interface text comes from the macOS fallback, which picks PingFang SC.
- Any list that puts a Latin font first (C, E, F) loses Chinese bold in a paragraph that starts plain: Avalonia 12.1.4 reuses the previous run's fallback typeface, weight included. This is the bug the comment in `Styles/Conversation.axaml` describes, and why answers put a CJK font first. Choosing C, E or F for answers needs a workaround first.
- In a family list, a later font is not used as the fallback: in F, Chinese falls back to PingFang SC, not to Noto Serif CJK SC. So a mixed Latin + Chinese pairing cannot be expressed as a simple family list.
- Hiragino Sans GB's regular face is W3 (weight 300), so it really is lighter.

Not verified: Windows (no PingFang there; Microsoft YaHei is the fallback), and how the subpixel option looks on a non-Retina screen.
