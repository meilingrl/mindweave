# Development machines

**Status:** facts about the owner's machines and what agents may do on them. Change this file when a machine, path or authorization changes. Progress is in [STATUS.md](../../STATUS.md).

## This Mac

- Daily data is in `~/Library/Application Support/Mindweave/claude-auto`. Use it only through `scripts/daily.sh`.
- The daily build opens from `~/Applications/Mindweave.app`. `publish` and `rollback` replace only its contents, so the Dock icon keeps working. See [daily build](daily-build.md).
- When a task worktree runs the app, set `MINDWEAVE_DATA_DIR` to that worktree's own folder.

## Windows 11, real hardware

- Use `scripts/windows.sh test|publish|sync|exec`.
- At home, use the SSH alias `win-lan` over the local network, which keeps long runs alive.
- Elsewhere, use host `win-home` through the SOCKS proxy `127.0.0.1:7898` of this Mac's userspace Tailscale instance. It drops long sessions. Login is by key.
- The folder is `E:\mindweave-claude-auto` (a historical name). `MINDWEAVE_WIN_DIR` can point elsewhere.
- `publish` uses the channel `dev` by default. `MINDWEAVE_WIN_CHANNEL=release` builds the Release build for the demo.
- Trial data from before 2026-11-16 is in the channel `claude-auto-dev`. To keep using it, set `MINDWEAVE_WIN_CHANNEL=claude-auto-dev`.
- Tasks and reports go to the Claude on Windows through the mailbox `E:\mindweave-mailbox\`. The protocol is in [mailbox.md](../mailbox.md).

## Authorized

- Pushing to `origin`.
- CI: the private repository runs Linux and Windows only.
