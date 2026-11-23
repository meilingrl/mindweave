# Version numbers and the change log

Approved in outline by the owner on 2026-11-23; this document is the written form, for the owner's approval before anything is built. It replaces the version rule in D-033 and changes the change-log rule in [AGENTS.md](../../AGENTS.md).

## 1. Why

- The version said too little. `main` had 95 merges between 2026-10-25 and 2026-11-23, and every build said 0.1.0 or 0.2.0.
- D-033 raised the version "at each finished plan step". Nobody can tell mechanically when that is, so the number did not move: four merges landed after 0.2.0 was set and none raised it.
- [changes.md](../changes.md) is keyed by day. An entry cannot be matched to the version a build shows in Settings, and nothing can refer back to an earlier change by a stable name.

The model is the Claude Code change log: one heading per version, one line per change, and a last number that rises with every release.

## 2. Goals and limits

Goals:

- The version a build shows names exactly one merge into `main`, one change-log entry and one git tag.
- Raising the version needs no judgment, except at a milestone, which the owner calls.
- The number shows how far the product has come.

Not goals:

- Semantic versioning. Nothing depends on Mindweave as a library, so the numbers do not promise compatibility.
- Numbering the merges before the changeover one by one. Before 2026-11-16 a merge was a track sync or a polish block, not a release.
- Changing the requirements document's own number (requirements v0.25 and the tags `baseline-v0.11`, `docs-baseline-v0.13`). It is a different number; prose writes "requirements v0.25" in full.

Must not happen: a change-log entry, a tag or a build that disagree about the version.

## 3. The number

The version is `first.second.third`.

| Part | Raised when | Who |
| --- | --- | --- |
| Third | Every merge into `main`, with no exceptions, documents-only merges included | Whoever merges |
| Second | A capability the owner would name to someone else is finished and on by default. The third number restarts at 0 | The owner calls it |
| First | 1.0.0 is the first build given to other people | The owner calls it |

- Work behind a switch raises only the third number. The merge that removes the switch may be the milestone.
- The version is a resource of which there can be only one (AGENTS.md, "Parallel agents", rule 3). It is taken when merging into `main`; task branches do not change it.
- The merge commit on `main` gets the tag `v<version>`.
- What Settings shows stays as D-033 set it: version, short commit, commit date and data channel outside a release; only the version in a release.

## 4. The past

Five phases are finished. Each boundary gets a tag on its historic commit; nothing is rewritten.

| Version | Dates | What the phase delivered | Boundary commit |
| --- | --- | --- | --- |
| 0.1.0 | 2026-10-25 to 10-28 | The core product, stages S0–S6: storage, real chat, branching and return, context budget and compression, summaries and versions, the summary network | `0887946` |
| 0.2.0 | 2026-10-29 to 11-04 | S7: four rounds of interface polish, the architecture remediation, the owner's Windows acceptance | `648aac8` |
| 0.3.0 | 2026-11-04 to 11-16 | The original requirements completed (items 3–8b): delete rules, workspace management, new-answer hints, suggested names, restore from backup, statistics, recommended questions, links; the structure refactor. Ends where the two tracks merged | `05b4252` (also `course-submission`) |
| 0.4.0 | 2026-11-17 to 11-20 | The agent platform: attachments, the run loop and local tools, web search, MCP; onboarding, icons, the context ring, the daily build | `6e5eb98` |
| 0.5.0 | 2026-11-20 to 11-22 | The bilingual product: English interface, prompts in the interface language, the reasoning fold, answer typography, living documents in English | `fbfddbd` |

- The merges after `fbfddbd` are 0.5.1, 0.5.2, … in first-parent order. There were eight on 2026-11-23; the changeover merge takes the next number, whatever it is by then. They get no tags and their entries are not edited.
- The session network is expected to be 0.6.0, in the merge that turns it on by default.
- The build that said "0.2.0" from 2026-11-22 is not the 0.2.0 of this table. The changeover entry says so.

## 5. The change log

From the changeover on, [changes.md](../changes.md) has one section per version, newest first:

```markdown
## 0.5.10 · 2026-11-24

- Session network (behind switch): Changed the spatial view to a molecule layout ([session-network.md](design/session-network.md) §6.1)
- Session network (behind switch): Changed titles to show whole, wrapped, in each dot's widest free angle
- Removed `SpatialPacking`; `SpatialLayout` replaces it
- Try: `MINDWEAVE_SESSION_NETWORK=1 scripts/daily.sh trial`
- Not verified: Windows; two explorations of 500 direct branches each take about 4.5 s
```

- The heading is the version and the merge date.
- Each change is one line that starts with Added, Changed, Fixed or Removed, in that order.
- A change that applies to one platform or sits behind a switch starts with a prefix: `Windows:`, `macOS:`, `Session network (behind switch):`.
- A line may name the decision, migration or design section it rests on. Detail longer than a line belongs in that design document.
- A line refers to an earlier change by its version: "Reverted 0.5.9's …".
- The last two lines are `Try:` and `Not verified:`. They are what the owner accepts a merge by, and the Claude Code change log has no equivalent. A merge with nothing to try says "Try: nothing on screen changed".
- A documents-only merge is one line that starts with "Documents:".
- A milestone version starts with one sentence naming the capability that is now finished.
- Entries are still not edited afterwards. The dated entries before the changeover stay as they are, under a heading that says they predate version headings.
- The top of the file carries the table in §4, with links to the [stage reports](../history/design/stage-reports.md) for 0.1–0.3.

## 6. Approaches considered

- **Milestone and merge counter (chosen).** It says how far the product has come and names every build, and the only judgment left is the milestone.
- **Start at 0.3.0 at the changeover, with no look back.** Simple, but the number would still hide five finished phases, which is the owner's complaint.
- **Date versions (2026.11.23).** They repeat the date Settings already shows and say nothing about milestones.
- **A single build counter.** Honest, but it loses the milestone.
- **Copy the Claude Code change log exactly.** It has no dates and no "Try" or "Not verified", which acceptance here depends on.

## 7. Checks

- A test fails when the first version heading in `changes.md` differs from `<Version>` in `Directory.Build.props`.
- A test fails when a version section lacks its `Try:` or `Not verified:` line, or has a change line that starts with another word than the four in §5 (after any prefix).
- The tag is not checked by a test, because tests run before the merge exists. The wrap-up step makes it.

## 8. What changes when this is built

- `Directory.Build.props`: the version at the changeover.
- `AGENTS.md`: the `docs/changes.md` rule under "Where files belong", and the version added to rule 3 and to the dispatcher's wrap-up under "Parallel agents". A rule change, committed on its own.
- [decisions.md](decisions.md): a new decision that replaces the first sentence of D-033.
- `docs/changes.md`: the version table, the heading over the dated entries, the first version section.
- Five tags on the boundary commits. Pushing them is an externally visible action and follows the owner's authorization.
- The two tests in §7.

Left to the builder: the exact wording of the tests' failure messages, and whether a small script takes the next number or the merger edits the file by hand.
