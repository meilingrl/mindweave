# Immutable synthetic format-5 compatibility fixtures

These fixtures remain in use for summary version and policy compatibility. `default/` and `disabled/` contain synthetic conversations and credential references only. No real vault, native database, supplier database or network was accessed. Full physical POST bodies came from an in-memory HttpMessageHandler using the real transport.

Generated before any Task 9 production edits, at HEAD `70f6c828150b8499639b2a4b99178e9b120c4aac`, whose production source exactly matched `57237c8`. [Pre-edit source evidence](pre-edit-source-evidence.log) preserves empty source diffs against both HEAD and that product baseline, plus all production C# SHA-256 values. [Generator source](GenerateGolden.cs.txt) and the complete [successful original log](generation.log) are retained; the temporary generator was removed from active test compilation after its two cases passed.

Each real SQLite store created five synthetic source exchanges, two sibling requesters, a saved configuration (context 16000, output 4096), two completed source-owned attempts/caches, and two prepared/completed replies. One summary contains 3330 ASCII background characters. The default configuration omits SummaryThinking; Disabled stores 1. Both format-5 summary requests have ContextSummary purpose, while ordinary prepared replies remain Standard. Schema stays 5 and prepared records retain private format 2.

Each manifest preserves full frozen configuration, originals, chunks, attempts, cache IDs/payload hashes, full application summary requests, prepared request/reference identity/hash, and three full raw POSTs (two summaries plus sibling reply). Tests copy the database into disposable directories, check unchanged whole-database bytes after read-only reopening, and separately delete the synthetic requester to prove source lifecycle independence. Old format-2/3/4 assets remain untouched. Never regenerate these golden expectations with new APIs.

Counts in each database: `{'conversation': 3, 'reply_context_plan': 7, 'context_compression_attempt': 2, 'context_summary_cache': 2, 'prepared_reply_request': 2, 'prepared_context_use': 12}`.

To reproduce in a separate checkout of the named baseline, copy `GenerateGolden.cs.txt` to `tests/Mindweave.Tests/GenerateFormat5Golden.cs`, select a fresh destination, and run:

```sh
MINDWEAVE_GOLDEN_DESTINATION="/absolute/new/destination" dotnet test tests/Mindweave.Tests/Mindweave.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~GenerateFormat5Golden --logger 'console;verbosity=detailed'
```

IDs and timestamps vary on regeneration. These fixtures prove API/storage/wire compatibility, not real model quality or native/course acceptance.

| File | SHA-256 |
| --- | --- |
| `GenerateGolden.cs.txt` | `9f0db58fa80f952bf82622d36e429aae67e6e8b0acbeeeca7a2a8647d589b1ca` |
| `default/format5.db` | `a6bd772e2529e79498369aee3b568a63fa8d8d1fe20a62de5ea01829a5cb15e6` |
| `default/manifest.json` | `4e1ea6e4eb1b4eafbc84dae1958495100e6e4b1910378dc2a2a29a1587e6fbc3` |
| `disabled/format5.db` | `b9670808359f5bfc8cc6625c7db6c5dcfa95a8c63678782dab9e40cd41330888` |
| `disabled/manifest.json` | `1e27898ad592656e4ea2f859d4dca7bfda9516826e63886267ac54c5162e283f` |
| `generation.log` | `59de2867174bf8ffce1070cbfef14f146edd6afd1245e60bc7eadcb19ddd37be` |
| `pre-edit-source-evidence.log` | `42a562ee08d33f36fd27446a7ce8027e28a40ed60e680d717a02e80a3d1c6249` |
