# Long-page passages (reply quality increment 3): the long-page evaluation, 2026-11-28

A long fetched page now reaches the model as its opening and the paragraphs that best match the question
([web-search.md](../../design/web-search.md) §6, [reply-quality.md](../../design/reply-quality.md) Q8). The `long-page`
scenario checks that the first `fetch_page` result the model gets already holds the figure deep in the page. The owner's
budget was USD 0.03: three runs, a cap of USD 0.01 each (2026-11-28).

`scripts/real-api.sh eval default long-page` on deepseek-flash (profile `default`, api.deepseek.com), the fake web, no judge,
prices 0.30 in / 1.20 out per million tokens, code `a6ee494`. Prompts: answer instructions 1, run instructions 4, summary 2.
The runs went one after the other.

| Run | File | Checks | Input / output tokens (reported) | From the cache | Cost |
| --- | --- | --- | --- | --- | --- |
| 1 | [long-page-run-1.md](long-page-run-1.md), [.json](long-page-run-1.json) | 5 / 5 | 2,936 / 162 | 2,048 (70%) | USD 0.0011 |
| 2 | [long-page-run-2.md](long-page-run-2.md), [.json](long-page-run-2.json) | 5 / 5 | 2,918 / 165 | 2,048 (70%) | USD 0.0011 |
| 3 | [long-page-run-3.md](long-page-run-3.md), [.json](long-page-run-3.json) | 5 / 5 | 2,915 / 147 | 2,048 (70%) | USD 0.0011 |

In every run the model searched, fetched the page once, and answered "9 年" with a valid citation [1]; the first page
result (about 606 characters) held "设计寿命为 9 年". No run searched the stored page afterwards. Total USD 0.0033.

Not covered: real pages, other models, and whether the other scenarios change (only `long-page` was run).
