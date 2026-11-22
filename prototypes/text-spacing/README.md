# Text spacing comparison

Status: a standalone, throwaway Avalonia prototype for tuning answer typography by eye. It is not product code and touches no data.

Started on 2026-11-21 as a font comparison. After trying seven font sets, the user kept the current fonts (Inter for the interface, PingFang SC first for answers): they keep bold working without making text too black. The tool now compares spacing and the other text settings instead.

## Run

```sh
cd prototypes/text-spacing
dotnet run -c Release
```

Avalonia 12.1.4 and LiveMarkdown.Avalonia 2.5.1, the same versions as the app; SDK 10.0.402.

## What it shows

Two or three panes side by side. Each is a conversation page with a question bubble and a real LiveMarkdown answer: headings, bold, a numbered and a bulleted list, inline code, a quote and long Chinese paragraphs. The answer family, the quote background and LiveMarkdown's colour keys are copied from `App.axaml`, `Styles/Conversation.axaml` and `Themes/Colors.axaml`, so a pane with every option on “now” draws what the app draws today.

Each pane has its own settings; option one is always today's value:

| Setting | Options |
| --- | --- |
| Line height | font default (now), 1.5×, 1.6×, 1.7×, 1.8× the size |
| Paragraph gap | 8 (now), 10, 12, 14, 16 px between blocks |
| Size | 14 (now), 14.5, 15 |
| Text colour | pure black or white (now), the app's primary text colour, two softer shades |
| Letter spacing | 0 (now), 0.2, 0.4 |
| Column | fill (now), 640, 720 px |
| Bold weight | Semibold (now; `**…**` asks for Bold, PingFang's heaviest face is Semibold), Medium |
| Bold colour | same as the text (now), softer in light / pure white in dark, brown, slate, sage, plum (muted; each tuned for light and dark) |
| Bold background | none (now), a subtle tint behind bold text |

A line under each pane's settings shows what the first paragraph really got: size, line pitch, gap and colour.

`--dump` lists every text block of the answer with its classes, size, weight and line height. `--shot <file.png>` writes a screenshot (rendered at 2x) and exits; `--three`, `--dark` and `--pane1 0,0,0,0,0,0` (option indexes in table order; also `--pane2`, `--pane3`) set the starting state. Screenshots in `screenshots/`: now · 1.7× with the app's primary colour · the same with a 12 px gap. Since 2026-11-22 screenshots are not kept in git (the owner's decision): redraw them with `dotnet run -- --three --shot screenshots/three-light.png` and the options above. The images the rounds below were judged on are in git history, for example `git show e602f4c:prototypes/text-spacing/screenshots/muted-light.png > muted-light.png`.

## Findings · 2026-11-21

- Answer line pitch today is 19.6 px at 14 px (1.4×, PingFang's own metrics); 1.7× gives 24 px.
- Answer text is pure black in the light theme (pure white in the dark one), not the app's primary text colour `#1C1917` / `#E6E8EE`. Answers set no colour, so they inherit the Fluent window colour; LiveMarkdown's `ForegroundColor` key does not reach paragraphs. Confirmed on a real app screenshot (`.claude/review-screenshots/10-web-search/web-03-citations-light.png`: the darkest answer pixels are `(0,0,0)`).
- When the line height is set, list numbers and bullets need the same line height, or they sit higher than their text.

Font findings from the first round (kept because they explain the choice): Inter draws only Latin interface text, from the Fluent theme's window font; Chinese interface text is PingFang SC through the macOS fallback. A family list that puts a Latin font first loses Chinese bold in a paragraph that starts plain (Avalonia 12.1.4 reuses the previous fallback typeface, weight included), and a later font in a family list is not used as the fallback.

Bold (2026-11-22). The user found bold too black and heavy in the light theme and not distinct enough in the dark one, where weight alone is not enough. LiveMarkdown draws `**…**` as a `Span` with the classes `Emphasis` and `Bold`; its weight is set on the element itself, so a style can change the colour and the background of bold text but not its weight. A lighter weight needs a few lines of code that set the weight after each render. Screenshots: `bold-light.png` (now · Medium · Medium with a background) and `bold-dark.png` (now · softer body with pure white Semibold bold · the same with Medium and a background).

Bold, second round (2026-11-22). The user chose a lighter weight plus a colour, and asked whether the colour can be a setting. Screenshots `colour-light.png` and `colour-dark.png`: Medium weight with amber, teal, violet and indigo. Indigo is close to the link colour. The user found them too vivid; they were replaced by muted brown, slate, sage and plum (`muted-light.png`, `muted-dark.png`).

Found on the way: `的**“火箭号”**在` is not bold. By CommonMark's rules, `**` between a Chinese character and a Chinese quotation mark neither opens nor closes emphasis, so the asterisks show. The app has the same behaviour.

Not verified: Windows (Microsoft YaHei has different metrics, so the same factor gives a different look), and the real app with these values applied.
