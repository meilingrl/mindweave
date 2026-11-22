# More platforms

**Status:** first draft, 2026-11-22. Not agreed. Taken from the opening brief of the owner's technical discussion of the same day; the owner's questions and answers from that discussion are still to be added.

**Related:** [evolution.md](../evolution.md) D3 (compatibility) and seam E1 (viewer slots); seam E5 (exchange format, a specification only).

## Goal

Mindweave on Linux, Android, iPhone, the Web and HarmonyOS, beside macOS and Windows.

## What the code allows

Checked in the code and the package cache on 2026-11-22:

- Core, Application and most of Infrastructure have no UI dependency. Every platform can reuse them.
- Desktop-only parts: the classic desktop lifetime, the single-instance lock, starting processes through `ISystemShell`, and MCP servers that run as local processes.
- `CredentialStores.ForPlatform` fails clearly on any system other than macOS and Windows. Each new platform adds one store behind it.
- **SQLite:** the native library covers Android, iOS and the browser.
- **Code highlighting is the first blocker on mobile and the browser.** LiveMarkdown highlights code with TextMateSharp, which needs the native Oniguruma library. That library ships for Linux, macOS and Windows only.
- **Touch:** LiveMarkdown has its own pointer-driven selection. The anchor layer only reads where the selection starts and ends. Touch needs a new input layer, not a new anchor model.

## Route, in order

| Platform | Route | Must be written | Main unknown and the smallest probe |
| --- | --- | --- | --- |
| 1. Linux | The same Desktop project, published for `linux-x64` | A libsecret credential store; AppImage or Flatpak packaging | The credential store under Flatpak. Probe: one test that saves and reads a key |
| 2. Android | A new head project with a one-column shell and the same view models | Touch selection (long-press, word hit-test, handles, then a branch button); a Keystore credential store; code highlighting replaced or turned off | Whether LiveMarkdown and anchors work with touch. Probe, one day: render one answer, long-press, create a branch anchor |
| 3. iPhone | A new head project with the same shell | An iOS Keychain store, close to the macOS one | Whether ahead-of-time compilation is clean. Probe: build the Android probe for iOS |
| 4. Web | The Avalonia browser target | Storage in the browser's private file system, or in memory with a copy in IndexedDB; a weaker key store; a small relay, because many AI services refuse calls from a browser | The browser runs on one thread, so code that blocks on async work hangs. Probe: start the app with SQLite and send one request |
| 5. HarmonyOS | The community OpenHarmony port of Avalonia: compiled ahead of time and loaded by a native shell | A credential store; packaging | Whether the port is alive; the signs are mixed. Probe: build its sample on a device |

## Across platforms

- **Narrow screens.** One column at a time: sidebar, then conversation, then network. The viewer-slot seam (E1) already separates navigation state from the view.
- **Generation in the background.** Android: a foreground service while an answer runs. iPhone: about 30 seconds, then the answer is cut; it is marked interrupted and the user runs it again. The existing checkpoints and final states support this.
- **Data between devices.** Each device keeps its own SQLite database. Sync goes through a log of changes, keyed by the stable IDs already in use, into a folder the user owns (iCloud Drive, WebDAV, Syncthing). There is no Mindweave server. The exchange format (E5) is the basis.
- **The relay again.** The Web target needs a relay for cross-origin calls. It can be the same relay as in the [free trial](../design/free-trial.md) design.

## Not verified

- Whether LiveMarkdown can turn code highlighting off.
- How well touch selection works.
- How SQLite performs in the browser's file system.
- Which AI services accept calls from a browser.
- Whether the HarmonyOS port runs the Avalonia version in use.
- Nothing has been built or run.

## Open questions

- Which platform after Linux: Android first, as the cheapest test of touch, is the draft's assumption.
- Whether the phone is a full Mindweave or a lighter companion (ask, read, branch; the network read-only).
- Whether sync must exist before the first phone build is useful.
- Distribution: stores, their costs and review rules.

## Smallest next step

The one-day Android probe. Its result sizes almost everything else on mobile.

## Sources

[Avalonia supported platforms](https://docs.avaloniaui.net/docs/supported-platforms), [Avalonia.Android](https://packages.nuget.org/packages/Avalonia.Android), [Avalonia.Browser](https://packages.nuget.org/packages/Avalonia.Browser/), [Avalonia.iOS](https://packages.nuget.org/packages/Avalonia.iOS/12.1.1), [OpenHarmony-NET](https://github.com/OpenHarmony-NET), [OpenHarmony.NET docs](https://openharmony-net.github.io/docs/en-us/), [Avalonia discussion 16377](https://github.com/AvaloniaUI/Avalonia/discussions/16377)
