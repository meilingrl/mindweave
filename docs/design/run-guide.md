# Run guide

How to build, run and test Mindweave.

## Requirements

- .NET SDK 10 (`global.json` pins major version 10 and allows newer feature bands). macOS (Apple silicon or Intel) or Windows 11 x64.
- The URL, model name and API key of a service compatible with OpenAI Chat Completions (for example DeepSeek). Without one, the app still opens and existing content can be browsed; only asking questions does not work.

## Build and test

```bash
dotnet test Mindweave.slnx -c Release
```

The full suite takes a minute or two on a Mac. Some tests must be turned on explicitly: real services (`scripts/real-api.sh test`, macOS only, key stored in the Keychain), system credentials, and the full-scale performance benchmark (`scripts/perf.sh`).

## Run

```bash
dotnet run --project src/Mindweave.Desktop -c Release
```

- **Data directory**: release builds (`dotnet publish`, and runs with `-c Release`) use the channel `release`: on macOS `~/Library/Application Support/Mindweave/release/`, on Windows `%LOCALAPPDATA%\Mindweave\release\`. All other builds (the default Debug runs and previews) use the channel `dev`; tests each use their own temporary data directory, unaffected by the channel. The `dev` data is in the sibling `dev/` directory, so development runs never open or upgrade the release build's data. The channel also determines the single-instance identity and the system credential entry (`Mindweave.release`/`Mindweave.dev`), so the API key is entered once for each. At the bottom of the settings page, release builds show only the name and version number; other channels also state the build's commit, its date and the channel. For a trial you can also point to another directory:

  ```bash
  MINDWEAVE_DATA_DIR=/tmp/mw-try dotnet run --project src/Mindweave.Desktop -c Release
  ```

- **First launch**: a short guide explains the product, connects a model (service URL, model and API key; this can be left for later) and creates the first workspace. The key is stored only in the system credential store (macOS Keychain, Windows Credential Manager); the database holds only a reference. When the credential service is unavailable, asking fails with a clear error; the key is never stored in plain text instead.
- **Upgrades**: before the database version is upgraded, the database is backed up automatically to `backups/` under the data directory, keeping at most 5 such copies; backups made before a deletion or a restore are kept until the user deletes them. If an upgrade fails, the app shows a recovery page; it never overwrites the data with a backup automatically.
- **Only one instance at a time**: a second launch brings the already open window to the front.

## Windows release

On Windows:

```bash
dotnet publish src/Mindweave.Desktop -c Release -r win-x64 --self-contained -o publish/win-x64
```

The output is a folder with its own runtime. Run `Mindweave.exe` in it directly; no separate .NET install is needed.

## Source package

```bash
scripts/package-source.sh
```

It packages the committed files with `git archive`, so bin/obj and run data are not included. The script checks that the size is no more than 10 MB, that there are no build outputs or data files, that the required files are all present, and that no development records or development-line names are in it, and it lists the contents.

## Where to start in the docs

- Requirements and architecture: `docs/requirements.md`, `docs/architecture.md`.
- In the repository (not in the source package): the design documents in `docs/design/`, the finished stage designs `s0-…` to `s7-…` in `docs/history/design/`, and the decisions in `docs/design/decisions.md`.
