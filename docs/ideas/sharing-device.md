# Sharing, step 3: device copies (design note)

**Status:** working draft, in discussion with the owner since 2026-11-26. Not adopted. It is the short note that [sharing.md](../design/sharing.md) §8 asks for before step 3's code. It builds on [sharing-import.md](../design/sharing-import.md). Decisions are the owner's unless marked as the writer's call.

## What a device copy is

The user saves a copy of their work on one device and imports it on another, by whatever means they have (sharing.md S3). Unlike a share it is for the same user: it holds everything, and on import what it brings is the user's own, not read-only (sharing.md S4, S7; sharing-import.md I2, I4).

## Decisions

### V1 What one copy covers (2026-11-26)

- A device copy holds all workspaces by default. The user may untick workspaces before saving; this is a list with ticks, not a view of its own.
- On import nothing is chosen: each workspace goes to the same workspace on the other device, by identity, and is created there when it is missing.
- An attachment original the device already has is not copied again.

Considered: one workspace per copy (smaller files, but one save and one import per workspace every time).

## Open

1. How the other device's continuation of the same conversation arrives.
2. Whether something deleted here can come back from the other device.
3. What stays on each device and never travels.
4. The package, the flow in the app, safety before an import that changes things, failure cases, acceptance, increments.
