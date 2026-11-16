#!/usr/bin/env bash
# The daily-use build of Mindweave on this Mac, kept apart from development (macOS only).
#
#   scripts/daily.sh publish [REF] [--accept-upgrade]  build REF (default main) as the new daily build
#   scripts/daily.sh trial [REF|current]               run a build on a copy of the daily data; the real data is untouched
#   scripts/daily.sh run                               start the current daily build
#   scripts/daily.sh rollback                          switch back to the previous daily build
#   scripts/daily.sh app                               (re)install ~/Applications/Mindweave.app from the current build
#   scripts/daily.sh backup                            copy the daily database to the backup folder
#   scripts/daily.sh status                            builds, data schema, latest backups
#
# Launch it from ~/Applications/Mindweave.app (Dock, Launchpad, Spotlight). That bundle stays at one path for good:
# publish and rollback replace only its Contents folder, so a Dock icon pinned to it keeps working across updates.
# Never pin anything inside Mindweave-daily/: the Dock resolves symlinks when you pin, so an icon dragged from
# current/ stays tied to one dated release, which a later publish deletes.
#
# The daily data is ~/Library/Application Support/Mindweave/claude-auto (channel "claude-auto", keychain service
# "Mindweave.claude-auto"). Every other build defaults to channel "dev", or "release" for Release builds
# (Directory.Build.props), so dev runs, tests and agent previews never open or migrate it. Only this script stamps the daily channel.
#
# publish only takes commits already merged into main, refuses while the daily app is open, copies the
# database to the backup folder first, keeps the previous build for rollback and records commit and schema version.
# A build with a newer schema upgrades the data on first launch (the app makes a verified backup before migrating);
# publish refuses it unless --accept-upgrade is given, which you should only pass after `trial` looked right.
#
# Environment: MINDWEAVE_DAILY_HOME (builds; default ~/Applications/Mindweave-daily),
# MINDWEAVE_DAILY_APP (the launcher bundle; default ~/Applications/Mindweave.app),
# MINDWEAVE_DAILY_BACKUP_DIR (database copies; default ~/Documents/Mindweave-daily-backups; best on another drive),
# MINDWEAVE_DAILY_DATA (daily data directory; for testing this script only).
set -euo pipefail

CHANNEL="claude-auto"
MAINLINE="main"
ROOT="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
APP_HOME="${MINDWEAVE_DAILY_HOME:-$HOME/Applications/Mindweave-daily}"
APP_BUNDLE="${MINDWEAVE_DAILY_APP:-$HOME/Applications/Mindweave.app}"
# Stable identity for Launch Services and the Dock; never change it, or pinned icons and settings tied to it go stale.
BUNDLE_ID="local.mindweave.daily"
LSREGISTER="/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister"
BACKUP_DIR="${MINDWEAVE_DAILY_BACKUP_DIR:-$HOME/Documents/Mindweave-daily-backups}"
DATA="${MINDWEAVE_DAILY_DATA:-$HOME/Library/Application Support/Mindweave/$CHANNEL}"
DB="$DATA/mindweave.db"
KEEP_RELEASES=3
KEEP_BACKUPS=60

die() { echo "daily: $*" >&2; exit 1; }
note() { echo "daily: $*"; }

[[ "$(uname -s)" == "Darwin" ]] || die "macOS only"
case "$(uname -m)" in
  arm64) RID="osx-arm64" ;;
  x86_64) RID="osx-x64" ;;
  *) die "unsupported CPU $(uname -m)" ;;
esac

# Schema version a commit's build writes: migrations are declared 1..n in SchemaMigrations.cs. Refuses to guess
# when the declarations do not read as a gap-free 1..n list.
schema_of_ref() {
  local versions expected=1 v
  versions="$(git -C "$ROOT" show "$1:src/Mindweave.Infrastructure/Sqlite/Migrations/SchemaMigrations.cs" \
    | sed -nE 's/^[[:space:]]+(Migration\.Sql|new)\(([0-9]+), ".*/\2/p')"
  for v in $versions; do
    [[ "$v" == "$expected" ]] || die "cannot read the schema version of $1 (found migration $v where $expected was expected)"
    expected=$((expected + 1))
  done
  (( expected > 1 )) || die "cannot read the schema version of $1 (no migrations found)"
  echo $((expected - 1))
}

data_schema() {
  if [[ -f "$DB" ]]; then sqlite3 "$DB" 'PRAGMA user_version;'; else echo 0; fi
}

release_field() { sed -n "s/^$2=//p" "$1/release.txt" 2>/dev/null; }

# The app holds instance.lock open for as long as it runs (dotnet run of an old daily-channel build included).
app_running() { [[ -n "$(lsof -t "$DATA/instance.lock" 2>/dev/null || true)" ]]; }

require_closed() {
  if app_running; then die "the daily Mindweave is open; close it first"; fi
}

same_volume_warning() {
  local a b
  a="$(df -P "$DATA" 2>/dev/null | awk 'NR==2 {print $1}')"
  b="$(df -P "$BACKUP_DIR" 2>/dev/null | awk 'NR==2 {print $1}')"
  if [[ -n "$a" && "$a" == "$b" ]]; then
    note "note: backups are on the same disk as the data; set MINDWEAVE_DAILY_BACKUP_DIR to another drive to survive a disk failure"
  fi
}

# Consistent copy through SQLite's backup API (safe while the app is open), checked before it counts.
backup_db() {
  local reason="$1" dest
  [[ -f "$DB" ]] || { note "no daily database yet; nothing to back up"; return 0; }
  mkdir -p "$BACKUP_DIR"
  dest="$BACKUP_DIR/mindweave-v$(data_schema)-$(date -u +%Y%m%dT%H%M%SZ)-$reason.db"
  sqlite3 "$DB" ".backup '$dest'"
  [[ "$(sqlite3 "$dest" 'PRAGMA quick_check;')" == "ok" ]] || { rm -f "$dest"; die "backup failed its integrity check; nothing changed"; }
  note "backed up: $dest"
  ls -1t "$BACKUP_DIR"/mindweave-v*.db 2>/dev/null | tail -n +$((KEEP_BACKUPS + 1)) | while IFS= read -r old; do rm -f "$old"; done
  same_volume_warning
}

# Publishes an exact commit (git archive, so no uncommitted or untracked files) stamped with the daily channel.
build_release() {
  local sha="$1" out="$2" src
  # Physical path: /var and /tmp are symlinks on macOS, and the build mixes both spellings otherwise.
  src="$(cd "$(mktemp -d "${TMPDIR:-/tmp}/mindweave-daily-src.XXXXXX")" && pwd -P)"
  git -C "$ROOT" archive "$sha" | tar -x -C "$src"
  note "building $(git -C "$ROOT" log -1 --format='%h %s' "$sha") for $RID ..."
  if ! dotnet publish "$src/src/Mindweave.Desktop" -c Release -r "$RID" --self-contained \
      -p:MindweaveChannel="$CHANNEL" -o "$out" -nologo -v quiet >"$src/publish.log" 2>&1; then
    tail -30 "$src/publish.log" >&2
    die "build failed (log: $src/publish.log)"
  fi
  rm -rf "$src"
  # The channel decides which data and keychain entries the build opens; check it really is the daily one.
  # The attribute blob is the length-prefixed key then the length-prefixed value, so this matches the exact value.
  perl -0777 -ne 'exit(/MindweaveChannel\x0bclaude-auto(?!-)/ ? 0 : 1)' "$out/Mindweave.dll" \
    || die "build is not stamped with channel $CHANNEL; refusing"
  {
    echo "commit=$(git -C "$ROOT" rev-parse "$sha")"
    echo "subject=$(git -C "$ROOT" log -1 --format=%s "$sha")"
    echo "schema=$(schema_of_ref "$sha")"
    echo "built=$(date '+%Y-%m-%d %H:%M:%S %z')"
    echo "rid=$RID"
  } >"$out/release.txt"
}

# Installs a release into the launcher bundle. The bundle folder itself is never replaced (the Dock's bookmark
# points at it); only Contents is swapped, by rename, so the bundle is never half old and half new. The copy is an
# APFS clone where possible, so it costs no extra disk space next to the release folder.
install_bundle() {
  local rel="$1" contents staging old
  [[ -x "$rel/Mindweave" && -f "$rel/release.txt" ]] || die "not a daily release: $rel"
  contents="$APP_BUNDLE/Contents"
  staging="$APP_BUNDLE/Contents.new"
  old="$APP_BUNDLE/Contents.old"
  mkdir -p "$APP_BUNDLE"
  # Finish or undo a swap an earlier run left half done.
  if [[ ! -d "$contents" && -d "$old" ]]; then mv "$old" "$contents"; fi
  rm -rf "$staging" "$old"

  mkdir -p "$staging/MacOS" "$staging/Resources"
  cp -cR "$rel/." "$staging/MacOS/" 2>/dev/null || cp -R "$rel/." "$staging/MacOS/"
  local commit version
  commit="$(release_field "$rel" commit)"
  version="$(sed -nE 's:.*<Version>([^<]+)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -1)"
  cat >"$staging/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Mindweave</string>
  <key>CFBundleDisplayName</key><string>Mindweave</string>
  <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
  <key>CFBundleExecutable</key><string>Mindweave</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleShortVersionString</key><string>${version:-0.0.0}</string>
  <key>CFBundleVersion</key><string>$(basename "$rel")</string>
  <key>MindweaveCommit</key><string>$commit</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.productivity</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSPrincipalClass</key><string>NSApplication</string>
</dict>
</plist>
PLIST
  plutil -lint -s "$staging/Info.plist" || die "generated Info.plist is invalid"
  echo "$(basename "$rel")" >"$staging/Resources/release-name"

  if [[ -d "$contents" ]]; then mv "$contents" "$old"; fi
  mv "$staging" "$contents"
  rm -rf "$old"
  # Tell Launch Services the bundle changed so the Dock, Spotlight and Finder show the new version at once.
  touch "$APP_BUNDLE"
  [[ -x "$LSREGISTER" ]] && "$LSREGISTER" -f "$APP_BUNDLE" >/dev/null 2>&1 || true
  note "launcher: $APP_BUNDLE -> $(basename "$rel")"
}

bundle_release() { cat "$APP_BUNDLE/Contents/Resources/release-name" 2>/dev/null || true; }

resolve_commit() {
  git -C "$ROOT" rev-parse --verify --quiet "$1^{commit}" || die "unknown ref: $1"
}

cmd_publish() {
  local ref="$MAINLINE" accept=0 arg sha short s_new s_data name rel previous
  for arg in "$@"; do
    case "$arg" in
      --accept-upgrade) accept=1 ;;
      -*) die "unknown option $arg" ;;
      *) ref="$arg" ;;
    esac
  done
  sha="$(resolve_commit "$ref")"
  short="$(git -C "$ROOT" rev-parse --short "$sha")"
  git -C "$ROOT" merge-base --is-ancestor "$sha" "$MAINLINE" || die "$short is not merged into $MAINLINE; the daily build only takes merged commits"
  require_closed

  s_new="$(schema_of_ref "$sha")"
  s_data="$(data_schema)"
  if (( s_new < s_data )); then
    die "$short writes schema v$s_new but the daily data is v$s_data; an older build cannot open it"
  fi
  if (( s_new > s_data && s_data > 0 && accept == 0 )); then
    die "$short upgrades the daily data from v$s_data to v$s_new on first launch (the app backs it up first).
       Try it on a copy first:   scripts/daily.sh trial $ref
       then publish with:        scripts/daily.sh publish $ref --accept-upgrade"
  fi

  mkdir -p "$APP_HOME/releases"
  name="$(date +%Y%m%d-%H%M%S)-$short"
  rel="$APP_HOME/releases/$name"
  rm -rf "$rel.partial"
  build_release "$sha" "$rel.partial"
  mv "$rel.partial" "$rel"

  require_closed
  backup_db "before-publish-$short"

  previous="$(readlink "$APP_HOME/current" 2>/dev/null || true)"
  if [[ -n "$previous" ]]; then ln -sfn "$previous" "$APP_HOME/previous"; fi
  ln -sfn "releases/$name" "$APP_HOME/current"
  install_bundle "$rel"

  # Keep current, previous and the newest few; release folders are self-contained (~100 MB each).
  local keep_cur keep_prev
  keep_cur="$(basename "$(readlink "$APP_HOME/current")")"
  keep_prev="$(basename "$(readlink "$APP_HOME/previous" 2>/dev/null || echo none)")"
  ls -1 "$APP_HOME/releases" | grep -v '\.partial$' | sort -r | tail -n +$((KEEP_RELEASES + 1)) | while IFS= read -r old; do
    [[ "$old" == "$keep_cur" || "$old" == "$keep_prev" ]] || rm -rf "${APP_HOME:?}/releases/$old"
  done

  if (( s_new > s_data && s_data > 0 )); then
    note "daily build is now $short (schema v$s_new); the data (v$s_data) is upgraded on its next launch"
  else
    note "daily build is now $short (schema v$s_new)"
  fi
  note "start it from the Dock or with: scripts/daily.sh run"
}

cmd_trial() {
  local ref="${1:-$MAINLINE}" app sha trial
  trial="$(cd "$(mktemp -d "${TMPDIR:-/tmp}/mindweave-trial.XXXXXX")" && pwd -P)"
  if [[ "$ref" == "current" ]]; then
    app="$APP_BUNDLE/Contents/MacOS"
    [[ -x "$app/Mindweave" ]] || die "no current daily build; run: scripts/daily.sh app"
  else
    sha="$(resolve_commit "$ref")"
    app="$trial/app"
    build_release "$sha" "$app"
  fi
  mkdir -p "$trial/data"
  if [[ -f "$DB" ]]; then
    sqlite3 "$DB" ".backup '$trial/data/mindweave.db'"
  fi
  note "trial data (a copy; delete when done): $trial/data"
  note "the trial build uses the daily keychain entries, so your saved API keys work in it"
  MINDWEAVE_DATA_DIR="$trial/data" exec "$app/Mindweave"
}

cmd_run() {
  [[ -x "$APP_BUNDLE/Contents/MacOS/Mindweave" ]] || die "no launcher yet; run: scripts/daily.sh publish (or app)"
  open "$APP_BUNDLE"
  note "started $(bundle_release)"
}

# Rebuilds the launcher from the current release without building anything (first install, or after a mishap).
cmd_app() {
  local cur
  cur="$(readlink "$APP_HOME/current" 2>/dev/null || true)"
  [[ -n "$cur" && -d "$APP_HOME/$cur" ]] || die "no current daily build; run: scripts/daily.sh publish"
  require_closed
  local s_cur s_data
  s_cur="$(release_field "$APP_HOME/$cur" schema)"
  s_data="$(data_schema)"
  (( s_cur >= s_data )) || die "the current build writes schema v$s_cur but the data is v$s_data; publish a newer build"
  install_bundle "$APP_HOME/$cur"
}

cmd_rollback() {
  local prev cur s_prev s_data
  prev="$(readlink "$APP_HOME/previous" 2>/dev/null || true)"
  cur="$(readlink "$APP_HOME/current" 2>/dev/null || true)"
  [[ -n "$prev" && -d "$APP_HOME/$prev" ]] || die "no previous build to roll back to"
  require_closed
  s_prev="$(release_field "$APP_HOME/$prev" schema)"
  s_data="$(data_schema)"
  if (( s_prev < s_data )); then
    die "the previous build writes schema v$s_prev but the data is already v$s_data.
       To go back, first restore the backup made before the upgrade (in the app: 设置 › 数据与存储 › 备份,
       or the matching file in $BACKUP_DIR); entries written since are lost. Then run rollback again."
  fi
  ln -sfn "$prev" "$APP_HOME/current"
  ln -sfn "$cur" "$APP_HOME/previous"
  install_bundle "$APP_HOME/$prev"
  note "daily build is now $(basename "$prev"); $(basename "$cur") kept as previous"
}

cmd_status() {
  local which
  for which in current previous; do
    if [[ -e "$APP_HOME/$which/release.txt" ]]; then
      echo "$which: $(basename "$(readlink "$APP_HOME/$which")")  schema v$(release_field "$APP_HOME/$which" schema)  $(release_field "$APP_HOME/$which" subject)"
    else
      echo "$which: none"
    fi
  done
  local installed
  installed="$(bundle_release)"
  if [[ -z "$installed" ]]; then
    echo "launcher: $APP_BUNDLE  not installed; run: scripts/daily.sh app"
  elif [[ "$installed" != "$(basename "$(readlink "$APP_HOME/current" 2>/dev/null)")" ]]; then
    echo "launcher: $APP_BUNDLE  runs $installed, NOT the current build; run: scripts/daily.sh app"
  else
    echo "launcher: $APP_BUNDLE  (current build)"
  fi
  echo "data:     $DATA  (schema v$(data_schema))$(app_running && echo ', open now')"
  echo "main:     $MAINLINE at $(git -C "$ROOT" rev-parse --short "$MAINLINE")  schema v$(schema_of_ref "$MAINLINE")"
  echo "backups:  $BACKUP_DIR"
  ls -1t "$BACKUP_DIR"/mindweave-v*.db 2>/dev/null | head -3 | sed 's/^/  /' || true
  same_volume_warning
}

case "${1:-}" in
  publish) shift; cmd_publish "$@" ;;
  trial) shift; cmd_trial "$@" ;;
  run) cmd_run ;;
  rollback) cmd_rollback ;;
  app) cmd_app ;;
  backup) backup_db manual ;;
  status) cmd_status ;;
  *) sed -n '2,10p' "$0"; exit 64 ;;
esac
