# Prompt caching step 2: the run evaluation before and after, 2026-11-27

The design asks for the run evaluation before the stable start of a request (prompt caching step 2, requirements §10.11.4,
architecture §15.5) merges, because instructions that stand at the end of a request may be followed differently
([ask-anywhere.md](../../design/ask-anywhere.md)). The owner's budget was USD 0.30 (2026-11-27).

`scripts/real-api.sh eval default` on deepseek-flash (profile `default`, api.deepseek.com), 20 scenarios, the fake web, no
judge, a cap of USD 0.15 per run (`MINDWEAVE_RUN_EVAL_BUDGET`), prices 0.30 in / 1.20 out per million tokens. The same
prompts in both runs (answer instructions 1, run instructions 4, summary 2); only the order of a request differs.

| Run | Code | File |
| --- | --- | --- |
| Before: the old order | `5e8e53f` (reply quality with main merged) plus the evaluation's cache row (`288a605`, cherry-picked) | [old-order.md](old-order.md), [old-order.json](old-order.json) |
| After: the stable start | `288a605` | [new-order.md](new-order.md), [new-order.json](new-order.json) |

The runs went one after the other, before first.

## Numbers

| | Before | After |
| --- | --- | --- |
| Checks passed | 75 / 79 | 74 / 79 |
| Scenarios with every check passed | 17 / 20 | 16 / 20 |
| Answers completed | 20 / 20 | 20 / 20 |
| Model requests, whole scenarios | 66 | 69 |
| Input / output tokens, whole scenarios (reported) | 132,535 / 50,046 | 147,563 / 49,751 |
| Of that input, read from the cache (reported) | 58,234 (44%) | 99,450 (67%) |
| Input not read from the cache | 74,301 | 48,113 |
| Cost at full input price (upper bound) | USD 0.0998 | USD 0.1040 |

- **The cache serves more.** Two thirds of the input came from the cache, against less than half before; the input that was
  not cached fell by about a third (74,301 → 48,113), although the run sent 11% more input in all (three more requests
  and longer material in some scenarios).
- **The "before" figure is not zero-based.** DeepSeek's cache is shared by every request of the account and keeps a prefix
  for hours; the reply quality runs earlier the same day sent the same scenarios in the old order, and the "after" run
  could reuse the start of the "before" run's system turn. Both figures are higher than a cold start would give; the
  difference between them is the signal.
- **The cost column does not show the saving**: it counts cached input at the full price. DeepSeek bills cached input at
  a much lower price, so the real bill of the "after" run is lower than its column says.

## Checks

The same misses in both runs: `follow-up` (月壤雷达, 地震计: the answer to the follow-up leaves out two instruments),
`two-readings` (59), `long-page` (9 years; it was found in 2 of 4 runs in the reply quality reruns).

One miss is new in the "after" run: `attachment-short` did not name the attachment. That rule is in the run
instructions, which did not move. Three more runs of that scenario on each order (about USD 0.001 each):

| | Main run | Rerun 1 | Rerun 2 | Rerun 3 | Named |
| --- | --- | --- | --- | --- | --- |
| Before | named | named | named | missed | 3 of 4 |
| After | missed | named | missed | named | 2 of 4 |

Both orders miss it sometimes; four samples cannot tell them apart.

No check shows the allowance or the instruction to answer being ignored: `limit-two` reached its request limit and
answered (3 / 3), and no run repeated a call.

Paid in all: about USD 0.21 at full input price (two runs and six short reruns), less on the bill.
