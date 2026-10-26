# Immutable synthetic format-3 compatibility fixture

This fixture remains in use for planner/protocol/storage compatibility tests. It contains only generated conversations and the test helper's synthetic credential marker. It never contacted an HTTP endpoint or system credential store.

Generated on 2026-10-27 (Asia/Singapore), before Task 6 production edits, using HEAD `9c09b2fc9a4846a8035db4993601a889160f1e07`. Its production source exactly matched `bc4a46c87cb94f351ed6d813bb584d21ac38d64b`; the baseline API creates format 3 with prompt `context-summary-v1` and summary cap 4096. The retained [pre-edit source evidence](pre-edit-source-evidence.log) records HEAD, empty baseline/current source diffs and source hashes. [Actual generator output](generation.log) records the successful test, artifact hashes and full generated manifest. [Generator source](GenerateGolden.cs.txt) is deliberately excluded from active compilation.

The real store APIs created a source with five synthetic question/2500-character-answer pairs, two sibling requesters, context budget 16000 and output 4096. Both source-owned attempts/caches completed with `stop`; one Background contains 3330 ASCII characters, deliberately above the new prompt's 1024-byte target. Both siblings prepared and completed replies. Database counts: schema 5, 3 conversations, 7 format-3 plans, 2 source attempts, 2 caches, 2 prepared records, 12 use references.

The manifest captures complete originals, accepted configuration, chunks, physical-request input values, attempts, completed cache identities/payload hashes, prepared request/references and stored prepared hash. Tests copy this database to disposable directories, read old materials with new code and compare the entire database bytes before/after disposal. Mutating/corruption cases operate only on those copies. Do not regenerate this fixture using new code or modify it to force cache regeneration. The separate format-2 fixture remains unchanged.

SHA-256:

| File | Hash |
| --- | --- |
| `format3.db` | `b222c1ca311c584c7e2e7b907b416e7053d2f111ab32ee66da2218a623427f0e` |
| `manifest.json` | `16885836cc97d8f839884ffa9a6203bffe1f0f1e25acf557080dcacc90b7f7f8` |
| `GenerateGolden.cs.txt` | `7386ceb08f6e813005de8c630729e80ebbf95267ca631fb23acdc9bbcf3db77d` |
| `generation.log` | `73bb4253f787ae4c3d3c8caed20c6b1d992014c85739fa5aa0e904b0c9f32627` |
| `pre-edit-source-evidence.log` | `72f8b6e54c22a00fbac02d35485b77a75832aa63408082be31c9fc9d412c1726` |

To reproduce the procedure, use a separate checkout of the named baseline, copy the generator into `tests/Mindweave.Tests/GenerateFormat3Golden.cs`, choose a new destination (never overwrite the committed fixture), and run:

```sh
MINDWEAVE_GOLDEN_DESTINATION="/absolute/new/destination" dotnet test tests/Mindweave.Tests/Mindweave.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~GenerateFormat3Golden --logger 'console;verbosity=detailed'
```

The generated IDs/timestamps vary; reproduction establishes the old API procedure, not identical database bytes. No live provider, transport or model quality acceptance is implied.
