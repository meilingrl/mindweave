#!/usr/bin/env bash
# Builds the source package for course delivery and checks it (handoff §5 S6 "交付包大小与内容核对").
#
#   scripts/package-source.sh [REF] [OUT]   REF defaults to HEAD, OUT to ./mindweave-source-<sha>.zip
#
# Uses git archive, so only committed, tracked files go in: no bin/obj, run data or local settings by construction.
# The checks still look, so a file committed by mistake is caught. Exits non-zero when a check fails.
set -euo pipefail

ROOT="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
REF="${1:-HEAD}"
SHA="$(git -C "$ROOT" rev-parse --short "$REF")"
OUT="${2:-$PWD/mindweave-source-$SHA.zip}"
LIMIT=$((10 * 1024 * 1024))

git -C "$ROOT" archive --format=zip --prefix="mindweave/" -o "$OUT" "$REF"

size="$(wc -c < "$OUT" | tr -d ' ')"
names="$(unzip -Z1 "$OUT")"
fail=0

if (( size > LIMIT )); then
  echo "FAIL size: $size bytes > $LIMIT" >&2
  fail=1
fi

# Build output, run data, credentials and editor or OS litter must never be in the package.
bad="$(printf '%s\n' "$names" | grep -E '(^|/)(bin|obj|\.vs|\.idea|artifacts|TestResults)/|\.(db|db-wal|db-shm|sqlite|user|suo|pfx|p12|key)$|(^|/)\.DS_Store$|(^|/)\._|\.env$' || true)"
if [[ -n "$bad" ]]; then
  echo "FAIL unwanted files:" >&2
  printf '  %s\n' $bad >&2
  fail=1
fi

# The things a reader needs to build and run it.
for required in mindweave/Mindweave.slnx mindweave/global.json mindweave/src/Mindweave.Desktop/Mindweave.Desktop.csproj mindweave/docs/design/run-guide.md; do
  if ! printf '%s\n' "$names" | grep -qx "$required"; then
    echo "FAIL missing: $required" >&2
    fail=1
  fi
done

# The package presents the product as Mindweave alone: no development process records (kept out by export-ignore
# in .gitattributes) and no development-line or channel names in any packaged file.
process="$(printf '%s\n' "$names" | grep -E '^mindweave/(TRACK\.md|STATUS\.md|AGENTS\.md|CLAUDE\.md|\.github/|docs/history/(development-routes|track-)|docs/reviews/(README\.md|claude[-]auto-))' || true)"
if [[ -n "$process" ]]; then
  echo "FAIL development records in the package:" >&2
  printf '  %s\n' $process >&2
  fail=1
fi
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
unzip -q "$OUT" -d "$scratch"
marked="$(grep -rIl -i -E 'claude[-]auto|codex[-]steady|track[/]|激进[轨]|稳健[轨]' "$scratch/mindweave" | sed "s|^$scratch/||" || true)"
if [[ -n "$marked" ]]; then
  echo "FAIL development-line names in packaged files:" >&2
  printf '  %s\n' $marked >&2
  fail=1
fi

# Relative links in packaged Markdown that point at files the package leaves out (reported, not failed). Review
# batches are frozen copies that keep the paths of their time, so they are not checked.
dead="$(cd "$scratch/mindweave" && find . -name '*.md' -not -path './docs/reviews/*' -print0 | xargs -0 perl -ne '
  while (/\]\(([^)#\s]+)(#[^)]*)?\)/g) {
    my $t = $1; next if $t =~ m{^[a-z]+:|^/};
    (my $dir = $ARGV) =~ s{[^/]*$}{};
    print "$ARGV -> $t\n" unless -e "$dir$t";
  }' | sed 's|^\./||' || true)"
if [[ -n "$dead" ]]; then
  echo "note: links to files outside the package:"
  printf '%s\n' "$dead" | sed 's/^/  /'
fi

files="$(printf '%s\n' "$names" | grep -vc '/$')"
echo "package: $OUT"
echo "commit:  $SHA"
printf 'size:    %s bytes (%s KB, limit 10 MB)\n' "$size" "$((size / 1024))"
echo "files:   $files"
echo "top-level contents (files per folder):"
printf '%s\n' "$names" | grep -v '/$' | awk -F/ '{ print (NF > 2 ? $2 "/" : $2) }' | sort | uniq -c | sort -rn | sed 's/^/  /'
exit "$fail"
