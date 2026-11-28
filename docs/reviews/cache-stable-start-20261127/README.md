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

## Request by request (2026-11-28)

Why do runs of several steps still read only about 70% from the cache? Four of them again on the stable start, with each
request's input and cached input in the JSON ([per-request.md](per-request.md), [per-request.json](per-request.json);
17 / 17 checks; USD 0.042 at full input price):

| Scenario | Answer requests: input → cached |
| --- | --- |
| web-auto | 955 → 768; 1,665 → 1,024; 2,172 → 1,664; 2,618 → 2,176 |
| follow-up (the second question) | 1,700 → 896; 2,943 → 1,792; 3,226 → 3,072 |
| long-page | 1,123 → 896; 2,699 → 1,152; 3,990 → 2,816; 5,308 → 4,224 |
| tight-context | 6,660 → 1,024; 7,463 → 6,784; 7,972 → 7,552 |

Each request after the first reads from the cache everything the previous request sent (DeepSeek counts in blocks of 64
tokens). What it does not read is what is new in that step: the model's reply, the tool results and the next note. No
cache can serve that. The first request of a question reads what earlier questions sent up to where they differ.
The attachment digests before tight-context's answer are separate requests with their own material.

Paid in all: about USD 0.25 at full input price (three runs and six short reruns), less on the bill.
