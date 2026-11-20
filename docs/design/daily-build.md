# Daily build (macOS)

The build the developer uses every day, with its data kept separate from both release builds and development runs. It is not published with the source package.

The daily build is built by `scripts/daily.sh`. Open it from `~/Applications/Mindweave.app` (it can be dragged to the Dock, and can also be found in Launchpad and Spotlight). Builds are stored in `~/Applications/Mindweave-daily/`, and data in `~/Library/Application Support/Mindweave/claude-auto/`:

```bash
scripts/daily.sh status                    # current and previous build, data version, latest backup
scripts/daily.sh publish                   # build a new daily build from the latest commit on main
scripts/daily.sh run                       # open the daily build
scripts/daily.sh trial                     # trial run on a copy of the daily data, without touching the real data
scripts/daily.sh rollback                  # switch back to the previous build
scripts/daily.sh app                       # reinstall Mindweave.app from the current build (first install, or when something breaks)
scripts/daily.sh backup                    # back up the database manually
```

- `publish` only accepts commits already merged into `main`, and refuses while the daily build is open. Before switching builds it backs up the database to `MINDWEAVE_DAILY_BACKUP_DIR` (default `~/Documents/Mindweave-daily-backups`; preferably point it at another disk), keeps the previous build, and records the commit and the database version.
- When a new build would upgrade the database (the database version goes up), `publish` refuses at first: first run `trial` to see that everything works on the copy, then add `--accept-upgrade`. The first time the app opens, it still makes a verified backup before upgrading.
- `rollback` switches only when the previous build can open the current data. If the data has already been upgraded, first restore the pre-upgrade backup in the app (content written since then is lost), then roll back.
- **The Dock icon never breaks.** The location of `Mindweave.app` never changes; `publish` and `rollback` only replace the `Contents` folder inside it (the new one is put in place first, then swapped in by renaming). The app folder itself does not change, so the icon pinned in the Dock always opens the current build. Copies use APFS clones and take no extra disk space. Do not drag anything inside `Mindweave-daily/` to the Dock: the Dock resolves a symbolic link such as `current` to one specific dated build, which a later `publish` deletes (this broke once on 2026-11-17: the icon was stuck on the v11 build from 11-13 and could not open the v13 data).
- The bundle identifier is fixed as `local.mindweave.daily`; do not change it. It has no icon of its own and uses the system's default app icon.
