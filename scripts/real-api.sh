#!/usr/bin/env bash
# Real AI service for development tests (macOS). Secrets never touch the repository, the command line or logs.
#
#   scripts/real-api.sh configure [profile]   enter address, model and key once (key goes to the login keychain)
#   scripts/real-api.sh test [profile]        run the opt-in real-service tests against that profile
#   scripts/real-api.sh list                  show configured profiles (never the key)
#   scripts/real-api.sh remove [profile]      delete a profile and its key
#
# The key is read by Apple's `security` tool from its own hidden prompt and stored as a generic password
# (service "Mindweave.claude-auto.dev", account = profile). Address and model live in
# ~/.config/mindweave-dev/claude-auto/<profile>.env. `test` passes the key only to the test process's environment.
# Real tests make a handful of short requests and may be billed by the provider.
set -euo pipefail

SERVICE="Mindweave.claude-auto.dev"
CONFIG_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/mindweave-dev/claude-auto"
ROOT="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
PROFILE="${2:-default}"

if [[ "$(uname)" != "Darwin" ]]; then
  echo "real-api.sh uses the macOS keychain; run it on the Mac." >&2
  exit 1
fi

case "${1:-}" in
  configure)
    mkdir -p "$CONFIG_DIR"
    chmod 700 "$CONFIG_DIR"
    read -r -p "Base URL (OpenAI-compatible, e.g. https://api.deepseek.com or https://api.example.com/v1): " base
    read -r -p "Model ID: " model
    [[ "$base" == https://* || "$base" == http://localhost* || "$base" == http://127.0.0.1* ]] || { echo "Use an https:// address." >&2; exit 1; }
    [[ -n "$model" ]] || { echo "Model ID is required." >&2; exit 1; }
    echo "Now paste the API key at the keychain prompt (input is hidden; you may be asked twice):"
    # -w as the last option makes `security` prompt for the secret itself; -U updates an existing item.
    security add-generic-password -U -s "$SERVICE" -a "$PROFILE" -l "$SERVICE ($PROFILE)" -w
    umask 077
    printf 'MINDWEAVE_REAL_API_URL=%q\nMINDWEAVE_REAL_API_MODEL=%q\n' "$base" "$model" > "$CONFIG_DIR/$PROFILE.env"
    echo "Saved profile '$PROFILE'. Run: scripts/real-api.sh test $PROFILE"
    ;;
  test)
    [[ -f "$CONFIG_DIR/$PROFILE.env" ]] || { echo "No profile '$PROFILE'. Run: scripts/real-api.sh configure $PROFILE" >&2; exit 1; }
    # shellcheck disable=SC1090
    source "$CONFIG_DIR/$PROFILE.env"
    key="$(security find-generic-password -s "$SERVICE" -a "$PROFILE" -w)" || { echo "Key for '$PROFILE' not found in the keychain." >&2; exit 1; }
    MINDWEAVE_REAL_API_URL="$MINDWEAVE_REAL_API_URL" MINDWEAVE_REAL_API_MODEL="$MINDWEAVE_REAL_API_MODEL" MINDWEAVE_REAL_API_KEY="$key" \
      dotnet test "$ROOT/tests/Mindweave.Infrastructure.Tests" --filter "Category=RealApi" --logger "console;verbosity=normal"
    ;;
  list)
    ls "$CONFIG_DIR" 2>/dev/null | sed -n 's/\.env$//p' || true
    ;;
  remove)
    security delete-generic-password -s "$SERVICE" -a "$PROFILE" >/dev/null 2>&1 || true
    rm -f "$CONFIG_DIR/$PROFILE.env"
    echo "Removed profile '$PROFILE'."
    ;;
  *) sed -n '2,15p' "$0"; exit 64 ;;
esac
