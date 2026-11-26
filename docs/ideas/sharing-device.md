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

### V2 What the other device added arrives as a branch where the two parted (2026-11-26)

The owner's three cases:

1. **Separate conversations.** Each device started new conversations. After the exchange both have all of them, in time order. Nothing conflicts.
2. **A line of branches.** Each device made a branch from the same conversation. After the exchange that conversation has both branches. Nothing conflicts.
3. **Questions inside one conversation.** Both devices kept asking in the same conversation. What the other device asked after the last question both share arrives as a continuation: a branch attached after that question (sharing-import.md I4), holding the other device's questions and answers.

- This replaces the "separate conversation" of sharing.md S4 for this case: the shared questions are not repeated, and the conversation's branches stay where they are. A branch that left from one of the other device's later answers hangs from the continuation.
- The other form, both devices' questions in one line by time, was set aside: each answer was written knowing only its own line, and with several questions on each side two trains of thought would be interleaved into something neither device saw.
- Each device keeps its own line as the conversation and shows the other's as the continuation, so the two devices' pictures differ in which is which. Nothing is lost on either side.
- A later copy adds the other device's further questions to that same continuation. Importing a copy again adds nothing.

## Open

2. Whether something deleted here can come back from the other device.
3. What stays on each device and never travels.
4. The package, the flow in the app, safety before an import that changes things, failure cases, acceptance, increments.
