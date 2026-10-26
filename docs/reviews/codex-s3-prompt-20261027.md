# Task 6 independent review

## Spec Compliance

- ✅ **SpecCompliance: Approved.** No missing, extra or misunderstood Task 6 implementation requirement was found in the fixed `9c09b2fc9a4846a8035db4993601a889160f1e07..2595b982a7c6496ac531455c316a3fde3cbf2448` package (1,602 lines, 212,980 bytes). It contains the controller-only acceptance-document commit `9124fa1` as well as implementation `2595b98`; the controller records are not attributed to the implementer.
- ✅ New acceptance uses format 4; only formats 2/3/4 are admitted. Format 2 maps to exact prompt-v1/min(1024, configured), format 3 to exact prompt-v1/min(4096, configured), and format 4 to prompt-v2/min(4096, configured): `src/Mindweave.Application/Chat/ReplyContextPlanner.cs:8`, `:12`; `src/Mindweave.Application/Chat/ReplyContextPlanner.Materials.cs:10`, `:16`, `:23`.
- ✅ The compact instruction retains the required exact first sentence, requests substantial compression into brief phrases, prohibits slice-by-slice retelling and repeated facts within/across fields, prioritizes effective facts/requirements/corrections with sources, and retains unresolved questions/references/pronouns. The entire JSON has a 1024 UTF-8 byte **soft target**, explicitly distinguished from exact byte counting: `src/Mindweave.Application/Chat/ContextSummaryProtocol.cs:19`.
- ✅ Old v1 instruction literals remain unchanged. Cache keys bind exact format and prompt plus configuration, source and ordered identity-bearing slices; the matcher also checks the version-specific cap. Standalone source requests authenticate candidate 4/3/2 identities without depending on a requester plan or lifecycle: `src/Mindweave.Application/Chat/ContextSummaryProtocol.cs:10`; `src/Mindweave.Application/Chat/ReplyContextPlanner.Materials.cs:26`, `:33`, `:42`; `src/Mindweave.Application/Chat/ReplyContextPlanner.cs:94`. Same-cap 3/4 keys, wrong-prompt known formats and unknown formats are rejected by focused tests: `tests/Mindweave.Tests/ContextPromptVersionTests.cs:68`.
- ✅ Parse/Serialize retain five unique string fields, nonblank-content validation and the 65536-byte ceiling. Nothing clips old content or treats the new target as a protocol limit. The new regression preserves 3330-character content and checks 65536-byte acceptance/65537-byte refusal: `src/Mindweave.Application/Chat/ContextSummaryProtocol.cs:11`, `:29`, `:37`, `:48`; `tests/Mindweave.Tests/ContextPromptVersionTests.cs:90`.
- ✅ Split preflight uses the actual versioned instruction and output reserve, and final composition validates the full material coverage/identity before checking actual inputs with the original output reserve. No soft-target bypass was introduced: `src/Mindweave.Application/Chat/ReplyContextPlanner.Materials.cs:75`, `:166`, `:200`; `src/Mindweave.Application/Chat/ReplyContextPlanner.cs:114`; `tests/Mindweave.Tests/ContextPromptVersionTests.cs:46`.
- ✅ The two old SQLite fixtures exercise exact chunks, full application summary-request JSON, cache IDs/content/hashes, prepared request/references, stored hash and private format 2. Whole database bytes remain equal after read-only test disposal. Separate tests cover requester deletion/source survival, new format 4 work without old-key reuse or old-plan rewrite, and corruption/unknown-version refusal: `tests/Mindweave.Tests/ContextBudgetVersionTests.cs:103`, `:136`, `:161`, `:181`, `:213`.
- ✅ Production changes are confined to the three Application planner/protocol files. There are no Core/ContextChunk shape, SQL v1–v5, prepared format/hash, coordinator, transport, UI, thinking, automatic-retry, provider/model exception or dependency changes. The test project only adds copied synthetic fixture assets: `tests/Mindweave.Tests/Mindweave.Tests.csproj:11`.
- ⚠️ **Cannot verify from this review: real model quality and desktop acceptance.** Deterministic prompt/version/storage tests do not establish brevity, fact retention, provider stop behavior, physical HTTP behavior, native old-data recovery, Windows or product/course acceptance. The controller must perform its authorized old-data-copy/native checks before deciding a bounded original-U3 retry. The implementation report explicitly retains this boundary: `.superpowers/sdd/s3-context-plan/task-6-report.md` (Limits and handoff).
- ⚠️ **Cannot independently attest historical execution timing.** The supplied generator, raw successful output, pre-edit source record, baseline source hashes and old-data RED results provide coherent provenance. They are retained implementation evidence, not an independently witnessed execution timeline: `tests/fixtures/context-format3/GenerateGolden.cs.txt:14`, `generation.log:14`, `pre-edit-source-evidence.log:1`. No required artifact is missing; this is an evidentiary boundary, not a defect.

## Strengths

- Prompt selection is one small explicit mapping shared by both key and request construction. The cap helper stays independent and unchanged, making format 3/4 equivalence in cap unable to erase prompt identity: `src/Mindweave.Application/Chat/ReplyContextPlanner.Materials.cs:16`, `:23`, `:33`, `:42`.
- The golden is genuinely independent of the new expected-value implementation: its generator asserts format 3, exercises real storage APIs, saves completed source-owned caches and both sibling prepared records, and deliberately includes an above-target valid old summary: `tests/fixtures/context-format3/GenerateGolden.cs.txt:14`, `:20`, `:35`, `:43`.
- The packing regression preserves the old format-3/6000-budget scenario and adds a format-4/6500-budget case, with complete original coverage/order, protected recent pair and final Fits assertions. It does not weaken production budgeting or shrink the test summaries to hide the longer prompt: `tests/Mindweave.Tests/ContextPlannerTests.cs:62`.
- Existing old-data tests were parameterized rather than duplicated; the new 101-line prompt test class has one focused responsibility. The implementation adds no parallel planner or persisted structure: `tests/Mindweave.Tests/ContextBudgetVersionTests.cs:89`, `:101`; `tests/Mindweave.Tests/ContextPromptVersionTests.cs:10`.

## Issues

### Critical (Must Fix)

None.

### Important (Should Fix)

None.

### Minor (Nice to Have)

None.

## Checks and evidence

- Read the complete fixed package in chunks; recovered truncated output only. Long synthetic repeated-character data was rendered with exact character/count notation to avoid tool-output truncation, without dropping fields or changing files. No Git diff was regenerated. Entry branch/status/worktree reads were performed once to satisfy repository rules; no later Git operation was run.
- The package ends protocol Parse, planner ComposeInputs and Materials SplitMessages hunks mid-function. Read focused continuations to inspect strict parsing, final Fits, version-specific preflight, and downstream plan validation; the named cross-cutting risk was that the prompt/key mapping might leave splitting or final composition using a different policy. Checked `ContextSummaryProtocol.cs:37–56`, `ReplyContextPlanner.cs:114–132`, and `ReplyContextPlanner.Materials.cs:56–218`; found consistent routing and retained full coverage/budget checks.
- Named risk **binary golden content and baseline provenance are invisible in a textual binary diff**: inspected only the tracked synthetic format-2/3 fixtures using SQLite URI `immutable=1&mode=ro`. Both reported integrity `ok`, zero FK violations, seven plans of their expected version, two completed source attempts (caps 1024/4096 respectively), two caches and two private-format-2 prepared records. Both entire database byte arrays were unchanged and neither produced sidecars.
- Format-3 inspection confirmed schema 5, three conversations, twelve prepared-use rows, exact manifest cache IDs/attempt IDs/content/payload hashes, Background lengths 3330 and 19, and the survivor's six source-quote/summary/verbatim references. Survivor prepared hash matched `432FF127410487A982C704A67327FA97F41AD89E2A9AD6612D206E84F7467EA9`; format-2 survivor hash matched `E19099F218039E28C9D9AE5FE045FE626F2F50EABF55A1C302F66E2907FDD6F8`. Fixture evidence is described at `tests/fixtures/context-format3/README.md:7` and `manifest.json:1`.
- Format-3 database SHA-256 matched `b222c1ca311c584c7e2e7b907b416e7053d2f111ab32ee66da2218a623427f0e`; manifest, retained generator, raw generation log and pre-edit evidence hashes all matched README values. The raw log's parsed complete manifest equaled the saved manifest. Original format-2 database and manifest hashes remained `7431fb81…9591` and `5db6b5ed…6a78`: `tests/fixtures/context-format3/README.md:15`, `generation.log:16`.
- As a focused provenance check, reversed the supplied three production-file hunks **in memory only**, checked each current hunk matched, and hashed the reconstructed base source. All three hashes exactly matched `tests/fixtures/context-format3/pre-edit-source-evidence.log:4–6` (`c6d2eb56…f0d40`, `3c501cf9…35e69`, `521415ef…76416`). No source file, Git object, index or HEAD was written.
- Inspected supplied execution logs, without rerunning tests: initial RED **15 failed / 26 passed** with expected 4 versus actual 3 (`task-6-red.log:15`, `:158`); packing regression **1 failed / 95 passed**, rejected by actual final Fits (`task-6-packing-regression.log:11`, `:18`); final focused GREEN **97 passed** (`task-6-focused-green.log:9`). All paths are under `.superpowers/sdd/s3-context-plan/`.
- Final supplied Release evidence is **328 passed / 2 existing explicit credential-platform skips / 0 failed**, total 330 (`task-6-full-release.log:8`, `:13`). Build is **0 warnings / 0 errors** (`task-6-build.log:8`); Python fixture is **8 passed** (`task-6-python.log:3`). No new warning or unexplained final-output noise was found. These are inspected implementer-run logs, not independent reruns.
- This reviewer ran no suite, native application, network/supplier call, credential/vault access or actual-user-data inspection. The only write is this requested ignored review report; no source, index, HEAD, fixture or acceptance record was modified.

## Assessment

**TaskQuality: Approved.**

**Counts:** 0 Critical, 0 Important, 0 Minor; 2 bounded cannot-verify items.

**Reasoning:** The small versioned-prompt change preserves exact legacy reconstruction and independently owned cache identity while retaining honest input/output budget enforcement. The synthetic old-storage and provenance evidence support task-scoped approval; the longer prompt can still make tight configurations infeasible, and actual model quality remains a separate controller gate.
