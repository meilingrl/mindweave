# More platforms

**Status:** first draft, 2026-11-22. Not agreed, except one decision: **no mobile development** (owner, said on 2026-11-22 and confirmed on 2026-11-24). Android and iPhone are out of this draft. The owner also said on 2026-11-22 that the direction may be Linux; that is not decided.

**Related:** [evolution.md](../evolution.md) D3 (compatibility) and seam E1 (viewer slots); seam E5 (exchange format, a specification only).

## Goal

Mindweave on Linux, the Web and HarmonyOS (computers), beside macOS and Windows.

## What the code allows

Checked in the code and the package cache on 2026-11-22:

- Core, Application and most of Infrastructure have no UI dependency. Every platform can reuse them.
- Desktop-only parts: the classic desktop lifetime, the single-instance lock, starting processes through `ISystemShell`, and MCP servers that run as local processes.
- `CredentialStores.ForPlatform` fails clearly on any system other than macOS and Windows. Each new platform adds one store behind it.
- **SQLite:** the native library covers the browser.
- **Code highlighting is the first blocker in the browser.** LiveMarkdown highlights code with TextMateSharp, which needs the native Oniguruma library. That library ships for Linux, macOS and Windows only.

## Route, in order

| Platform | Route | Must be written | Main unknown and the smallest probe |
| --- | --- | --- | --- |
| 1. Linux | The same Desktop project, published for `linux-x64` | A libsecret credential store; AppImage or Flatpak packaging | The credential store under Flatpak. Probe: one test that saves and reads a key |
| 2. Web | The Avalonia browser target | Storage in the browser's private file system, or in memory with a copy in IndexedDB; a weaker key store; a small relay, because many AI services refuse calls from a browser | The browser runs on one thread, so code that blocks on async work hangs. Probe: start the app with SQLite and send one request |
| 3. HarmonyOS | The community OpenHarmony port of Avalonia: compiled ahead of time and loaded by a native shell | A credential store; packaging | Whether the port is alive; the signs are mixed. Probe: build its sample on a device |

## Across platforms

- **Data between devices.** Each device keeps its own SQLite database. Sync goes through a log of changes, keyed by the stable IDs already in use, into a folder the user owns (iCloud Drive, WebDAV, Syncthing). There is no Mindweave server. The exchange format (E5) is the basis.
- **The relay again.** The Web target needs a relay for cross-origin calls. It can be the same relay as in the [free trial](../design/free-trial.md) design.

## Not verified

- Whether LiveMarkdown can turn code highlighting off.
- How SQLite performs in the browser's file system.
- Which AI services accept calls from a browser.
- Whether the HarmonyOS port runs the Avalonia version in use.
- Nothing has been built or run.

## Open questions

- Whether Linux is the next platform, and which comes after it.
- Distribution: stores, their costs and review rules.

## Smallest next step

For Linux: one test that saves and reads a key in the system's credential store.

## Sources

[Avalonia supported platforms](https://docs.avaloniaui.net/docs/supported-platforms), [Avalonia.Android](https://packages.nuget.org/packages/Avalonia.Android), [Avalonia.Browser](https://packages.nuget.org/packages/Avalonia.Browser/), [Avalonia.iOS](https://packages.nuget.org/packages/Avalonia.iOS/12.1.1), [OpenHarmony-NET](https://github.com/OpenHarmony-NET), [OpenHarmony.NET docs](https://openharmony-net.github.io/docs/en-us/), [Avalonia discussion 16377](https://github.com/AvaloniaUI/Avalonia/discussions/16377)
