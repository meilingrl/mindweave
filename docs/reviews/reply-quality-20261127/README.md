# Reply quality increment 1: the first real comparison, 2026-11-27

`scripts/real-api.sh compare default` with the judge (`MINDWEAVE_RUN_EVAL_JUDGE=1`), at commit 8aa75da, on deepseek-flash
(profile `default`, api.deepseek.com). Previous prompts (no answer instructions, run instructions 3, summary 1) against the
current ones (answer instructions 1, run instructions 4, summary 2), 20 scenarios each, the fake web. One run; the owner's
budget was USD 1 (2026-11-27), capped in the run at USD 0.90.

| File | What |
| --- | --- |
| [deepseek-flash.md](deepseek-flash.md) | The side-by-side report: totals, one row per scenario, each scenario's checks and both answers |
| [deepseek-flash.json](deepseek-flash.json) | The same run's numbers |
| [tight-context-digest-fix.md](tight-context-digest-fix.md) | The check of the digest fix: tight-context alone, current prompts |
| [tight-context-digest-fix.json](tight-context-digest-fix.json) | Its numbers |
| [long-page-rerun-1.md](long-page-rerun-1.md), [-2](long-page-rerun-2.md), [-3](long-page-rerun-3.md) (and .json) | long-page three more times, current prompts |

## Cost

At the peak list price (USD 0.30 in, 1.20 out per million tokens; cached input counted at full price, so this is an upper
bound): previous 0.061, current 0.078, judge 0.051, **about USD 0.19 in all**. 16 minutes.

## What it shows

- **Checks: 59 / 72 → 64 / 72** on the scenarios both editions completed. The report's 60 / 79 → 71 / 79 also counts
  no-info and limit-two, where the previous edition's only request failed with an SSL error: a network fault, not the prompt.
- Gains with the current prompts: summary-jumps (the summary opens with a gist and lists 8 key terms; the previous one opened
  with a heading and had no terms), branch-term and branch-paragraph (no `[1]` without a source), attachment-short (names
  the attachment), attachment-long (no stray `[1]`).
- **long-page lost its answer** with the current prompts: the model fetched the page, then searched the saved page and
  concluded that 青鸾-7 is not in it. The previous prompts found "9 years". One sample; the same model varies between runs.
- **tight-context fails in both editions** before any answer, while making a digest of the long attachment: the previous
  run timed out; the current one hit the output limit (10,106 output tokens over the digest requests; the limit per
  request is 8,192). Digests still get cut off on this reasoning model: context summaries and summaries use 16,384 and
  retry once on a cut-off, digests do neither.
- Cost of the instructions: input tokens +31 % on the answers under test (about 177 more per request, plus more tool calls in
  the network scenarios), output tokens +40 %. Time to first text −9 % in total; one sample.
- Judge (hints only): clearer answer, current 9, previous 4, inconsistent 3, unreadable 1. Its support check marks few
  cited sentences as fully supported in either edition (previous 7 / 28, current 4 / 38, most "partly"); read it as weak.

Whether the answers read better is the owner's reading of the report.

## The digest fix, checked (2026-11-27)

The digest fix (0dbef0b: 16,384 output tokens, or all but 4,000 tokens of input in a small window, and one more request
after a cut-off) was checked with `scripts/real-api.sh eval default tight-context` at a85ee43, on the owner's word, with a cap
of USD 0.05 counted per request. **tight-context completed with 4 / 4 checks**: the answer names 邓远山 from the attachment and
玉衡-7 from the web, with 2 valid citations. 8 requests in all (3 for the answer), 32,796 input and 13,382 output tokens,
**about USD 0.026** at the peak price, 90 seconds. The report does not show whether a digest request needed the second try.

## long-page, three more runs (2026-11-27)

On the owner's word, `scripts/real-api.sh eval default long-page` three times at 812edce, current prompts, a cap of USD 0.01
each: **found "9 years" in 2 of 3** (runs 2 and 3), missed it in run 1 the same way as in the comparison. With the
comparison, the current prompts found it in 2 of 4 runs; the previous prompts in 1 of 1. About USD 0.016 in all.

How the model gets there: the page opens with its first 2,000 tokens, which hold nothing about 青鸾-7. The model's searches
of the stored page with the right words ("青鸾-7 设计寿命") did not bring up the passage in any run; the runs that found it
read parts 8 to 10 one by one until they hit it. So the outcome turns on guessing which part to read, not on the prompt's
stop lines. Increment 3 (Q8: a long page's best-matching passages, Chinese matched by two-character pieces) aims at exactly
this. Why the stored-page search misses the passage was not looked into.

Run 2 opens with "按'该报告里写明的官方参数'这一理解回答" ("taking it to mean the figure stated in the report"): the
two-readings line firing on a question with one reading.

