# Synthetic schema-5 / S3 request-format-6 baseline

In use for S4 migration and immutable request compatibility. This fixture is entirely synthetic; no native/user/supplier database, vault or network was accessed. Both `default/` and `disabled/` were generated **before any Task 2 Infrastructure or SQL edits** at HEAD `279ea2750c1c8e76e9b869c6fa674770693daf00`. Its S3 Infrastructure/Application Chat/Core Chat source equals product `1f9c6bd`; Task 1 adds separate summary domain contracts and does not modify the old S3 path.

- [Pre-edit evidence](pre-edit-source-evidence.log) records HEAD, empty production working diff, empty S3 source diff to `1f9c6bd`, source hashes, and hashes of all 24 original format-2–5 fixture assets.
- [Generator](GenerateGolden.cs.txt) is the exact generator used, kept outside active test compilation. [Original generation log](generation.log) records two passing cases and all synthetic manifests. The temporary generator was removed before product edits.
- Each `schema5.db` has schema 5, S3 request format 6, private prepared format 2, five conversations, saved configuration and draft, terminal and active Reply jobs, completed and active source-owned compression, caches, plans and prepared references. Frozen IDs, actual request bytes, cache and prepared hashes are in the corresponding `manifest.json`.
- Requests passed through the real serializer into a controlled in-memory HTTP handler. The handler never opened a socket; credential strings are arbitrary synthetic text, with no vault use.

Tests copy these files to disposable roots. Upgrading 5→6 changes the copy's database bytes legitimately; the test compares **every value of every original table row**, checks the schema-5 backup, then captures a post-migration byte baseline for read-only reopen/reconstruction. The complete request/configuration/cache/prepared comparisons remain exact. Original fixtures are never rewritten, re-versioned or repaired. Existing format-2–5 source assets remain byte-identical.

Reproduce only in a separate checkout of the named baseline: copy the generator to `tests/Mindweave.Tests/GenerateSchema5Format6Golden.cs`, set `MINDWEAVE_GOLDEN_DESTINATION` to a fresh directory and run `dotnet test tests/Mindweave.Tests/Mindweave.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~GenerateSchema5Format6Golden --logger 'console;verbosity=detailed'`. IDs/timestamps naturally differ; do not regenerate committed expectations on new production code.

| File | SHA-256 |
| --- | --- |
| `GenerateGolden.cs.txt` | `1e5e8b4c411bb562e213949b46efbb393af99d2d440b9aca76c9e89419fdc483` |
| `default/manifest.json` | `32875c167516f92199568e979e18346574a5856354dbd0868dc22971df426454` |
| `default/schema5.db` | `4f78f953cd7b3c9abfd57d96e18f38e66f50a673d3c060be4dd7d1590c92c596` |
| `disabled/manifest.json` | `9381c754189296e89b31f6b2b537fe3cc069b9c8d3187cc42398987a258ee2c4` |
| `disabled/schema5.db` | `9ed479ca3fb2ff9786b31ca266d7ce24f1d09a4d10093b8383270a4a26e8c649` |
| `generation.log` | `9eaeae35e5b587038e62fb7ae9c96f965a64bee4addab5ad40a8752d5fe7fd15` |
| `pre-edit-source-evidence.log` | `1f3c25c766d5f19a27b34ed7a8f8d98a54b804f581cd270dca3b282d87c25363` |
