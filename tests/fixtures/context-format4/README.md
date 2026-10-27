# Immutable synthetic format-4 compatibility fixture

This fixture remains in use for summary policy/version compatibility. It contains generated conversation text and synthetic credential references only. No external endpoint, real credential store or user/native/supplier database was accessed. The transport capture uses an in-memory HttpMessageHandler.

Generated on 2026-10-27 before Task 7 production edits, at HEAD `5c591df1d08739631fa11fb462a10b1c48145dc3`. Production source matched `2595b982a7c6496ac531455c316a3fde3cbf2448` exactly. The [pre-edit evidence](pre-edit-source-evidence.log) retains both empty source diffs and all production C# SHA-256 values. The [generator source](GenerateGolden.cs.txt) and [successful raw run](generation.log) are preserved. The generator used old APIs only, then was removed from active test compilation.

The real SQLite store created five synthetic source exchanges, two sibling requesters, a saved default configuration (context budget 16000, output 4096), completed source-owned attempts/caches, and two prepared/completed replies. One summary has 3330 ASCII characters. Schema remains 5; plans are format4/prompt-v2; prepared records remain private format2. Counts: {"conversation": 3, "reply_context_plan": 7, "context_compression_attempt": 2, "context_summary_cache": 2, "prepared_reply_request": 2, "prepared_context_use": 12}.

The manifest records complete frozen configuration, originals, chunks, attempts, cache identity and payload hashes, full summary ChatRequest JSON, prepared request/references/hash, plus complete raw transport POST bodies captured for every summary and the sibling reply. Tests copy the database into disposable directories and verify full bytes unchanged after read-only reopening. The format2/3 fixtures remain untouched. Do not regenerate this golden with newer APIs or overwrite it to accommodate a changed expectation.

To reproduce the procedure in a separate checkout of the named baseline, copy `GenerateGolden.cs.txt` into `tests/Mindweave.Tests/GenerateFormat4Golden.cs`, choose a fresh destination, and run:

```sh
MINDWEAVE_GOLDEN_DESTINATION="/absolute/new/destination" dotnet test tests/Mindweave.Tests/Mindweave.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~GenerateFormat4Golden --logger 'console;verbosity=detailed'
```

IDs and timestamps vary on regeneration. This proves API/storage/wire compatibility, not provider quality, native UI or course acceptance.

| File | SHA-256 |
| --- | --- |
| `GenerateGolden.cs.txt` | `36067cdc04371485d9705ec00f63ac24ba9e3c1708159d5fba18a9e3763859a9` |
| `format4.db` | `224de7018a94bf7f03f2cf45331d406d0657e810ea0233dc7a2fdc0ea16f2bea` |
| `generation.log` | `62996650ed697a0dfd3b625265dae0cfecc1148c69304c62b2b28f8ebb344654` |
| `manifest.json` | `ed28cd124cfcca9f534b8f0d0f323006199ab29149d7e9d1efb970708cb703de` |
| `pre-edit-source-evidence.log` | `47976029265a598b8a02a8aea010b1b8d8204c95f67759e4eb45cc208492bb2a` |
