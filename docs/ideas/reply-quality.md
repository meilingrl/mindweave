# Reply quality

**Status:** in discussion with the owner since 2026-11-25, reopened on 2026-11-26 at Q5; decisions are recorded under "Discussion". Nothing outside that section is agreed. First draft 2026-11-22. Based on a research pass of the same day over open-source projects: their prompts, code and documents were read; the adaptations to Mindweave and all cost estimates are inferences, not measurements.

**Related:** [agent-loop.md](../design/agent-loop.md) (run loop, L1–L12, section 8), [web-search.md](../design/web-search.md), [requirements §10.4](../requirements.md).

## Discussion

Started 2026-11-25. The owner decides; each decision is recorded here with its date. Questions are asked one at a time, in the order of what depends on what.

### Approach in short

One answer prompt opens every answer, in the interface language, with a version number. Its lines are defaults; the user's question wins. Pieces join it by condition: a branch paragraph in a branch, the user's note when there is one, the titles of matching summaries when there are any and the tool section when tools are offered. The summary prompt gets a gist, a body by concept and key terms. A long page reaches the model as its best-matching passages. Nothing here adds a model request to an answer. The evaluation set measures all of it, with cost and time, and the owner's reading and daily use decide what is good.

### Order of the questions

| # | Question | Depends on |
| --- | --- | --- |
| Q1 | Does every answer get an answer prompt, and how strongly does it steer? (idea 1) | Nothing; the others hang on it |
| Q2 | The rule for branch answers (idea 1) | Q1 |
| Q3 | Self-check cards and a guide-me mode (dropped, see the decisions) | Q1 |
| Q4 | What the user already knows, and memory (idea 7, "Memory") | Q1 |
| Q5 | Follow-up suggestions per answer (idea 2; dropped, see the decisions) | Q1, Q2 |
| Q6 | The tool section: stop and refine rules, naming an attachment (idea 5; agent-loop §8) | Q1 (the run prompt becomes "answer prompt + tool section") |
| Q6b | The clarification card (idea 8) | Q1 |
| Q7 | Rewriting follow-up questions (idea 4; reopens L9, decided 2026-11-19) | Q6 |
| Q8 | Relevant passages from fetched pages (idea 3) | Nothing |
| Q9 | Better summaries (idea 10) | Nothing |
| Q10 | The evaluation of explanations and of citations (ideas 6, 9) | What Q1–Q9 keep |
| Q11 | The increments; then the owner says whether the draft is agreed | All |

### Decisions

- **Q1, 2026-11-25 (owner): every answer gets one short answer prompt.**
  - Content: what Mindweave is; answer in the language of the question; core idea first, then build up; define a term at first use; an example or analogy when it helps; length scaled to the question.
  - The lines are defaults. When the user's question asks for something else, the question wins.
  - The run prompt becomes "answer prompt + tool section", so answers with and without tools explain in the same way.
  - One file per language, with a version number, like the run prompt.
  - Accepted cost: about 300 input tokens on every answer request; every answer changes at once on every model; whether answers read better is judged with a real model and by the owner's reading.
- **Q2, 2026-11-25 (owner): a branch paragraph in the answer prompt.**
  - Added only when the conversation is a branch, to every answer in it.
  - It says: the user selected this passage in the answer above and opened a branch to explore it; explain the passage in the context it came from; do not answer the parent question again or repeat what the parent answer said; if the user typed a question about the passage, answer that question.
  - Length is scaled to the passage and the question. The first draft's "stay shorter than the parent" is dropped.
  - A deeper branch gets the same paragraph; it names only the nearest selection.
  - Accepted cost: about 60 input tokens in branches; a branch used as a fresh start stays tied to the passage until the question says otherwise.
- **Q3, 2026-11-25 (owner): no self-check cards and no guide-me mode.** Both are dropped outright, in any form: no card, no mode and no line in the answer prompt about quizzing or guiding. Requirements §5.4 stays as written.
- **Q4, 2026-11-25 (owner): the answer prompt carries a note the user writes and the titles of matching summaries. No extracted facts.**
  - The note: one free-text field in Settings, at most about 500 characters, one for all workspaces. It goes into every answer's prompt, marked as the user's own preferences. No model call; nothing is derived. It is not shared.
  - The titles: before each answer, one local keyword search of this workspace's summaries with the question and, in a branch, the selected passage. The top three titles above a match threshold go into the prompt with "build on these; do not explain them again from the start". Titles only; with tools on, the model can open one through the summary search.
  - Facts extracted by the model (level 3 of "Memory") are not built.
  - Accepted cost: up to about 200 input tokens for the note and about 50 for the titles; one stored preference; a Settings field whose look the owner confirms; a wrong match can make the model skip a needed explanation; nothing on screen shows which titles were sent; the Settings text on what is sent to the AI (requirements §9.4) gains a line.
- **Q5: reopened, 2026-11-26.** The owner said yes on 2026-11-25 to follow-up suggestions under each answer, withdrew that on 2026-11-26 ("keep answers plain; no need to offer possible follow-up questions"), and reopened the discussion the same day because the record of the withdrawal went further than what was said. Three things are kept apart:
  - **A. Exploration recommendations in a workspace**: recommended questions, recommended nodes and conversations started from a recommendation (requirements §10.3.1, §10.3.2, §10.3.5). Built, and outside this draft. Nothing here changes or removes them.
  - **B. Chips under an answer**: the mechanism first agreed as Q5 (a marked block in the response, parsed and shown as chips). Withdrawn by the owner.
  - **C. What the model writes at the end of its own answer text**: for example a closing sentence that points to a related direction. The owner has decided nothing about this. Open.
- **Q6, 2026-11-25 (owner): five lines join the tool section; the run prompt goes to version 4.**
  - Stop: answer once the material answers the question; about three relevant sources is usually enough; if two searches in a row bring nothing new, stop and say what is missing.
  - Refine: when a search falls short, search again with terms found in the results, not with the same words.
  - Search terms: plain words, no search operators.
  - Sources: prefer the original source over a page that retells it.
  - Attachments: when relying on an attachment that came with the question and has no number, name it; write a number only for results that carry one. This closes the second to-do in section 8 of agent-loop.md.
  - Accepted cost: about 70 input tokens in answers with tools; "about three sources" can stop a model early on a question that needs breadth; one evaluation run with a real model compares versions 3 and 4, within a budget the owner gives.
  - The first to-do in section 8 of agent-loop.md (attachment digests cut by the output limit on reasoning models) is an internal defect: it goes into the first increment as a fix with a failing test first.
- **Q6b, 2026-11-25 (owner): no clarification card; one line in the answer prompt.**
  - When a question has two clearly different readings and the answer would differ, the model says in one sentence which reading it takes and names the other, then answers the one it took.
  - It works for every answer, with or without tools, and nothing pauses.
  - Accepted cost: about 30 input tokens; a wrong pick costs one more question and answer.
  - Not chosen: a card that pauses the run and waits for a choice (idea 8).
  - Open since 2026-11-26: the other reading was to be offered as a chip (Q5). With the chips withdrawn, the wording above (the other reading named in the sentence) is a proposal, not yet confirmed by the owner.
- **Q7, 2026-11-25 (owner): follow-up questions keep the L9 rule; no model request rewrites them.**
  - The summary title search of Q4 uses the same rule as the pre-search: for a short or referring question, the conversation title and the branch selection go in front. One rule, in one place, serves both searches.
  - The evaluation set gains a follow-up scenario in the "开" (On) web setting, so the rule is measured on a real service.
  - Accepted cost: queries for follow-ups stay rough ("title + question").
  - Not chosen: a small request that rewrites the question to stand alone (idea 4).
- **Q8, 2026-11-26 (owner): for a long page the model gets the best-matching passages, within the same 2,000 estimated tokens.**
  - The model gets the opening of the page (title and lead, about 300 tokens), then the best-matching passages in page order with a mark where text is left out, then the handle as today.
  - Matched against the question (with the Q7 rule for follow-ups) and the terms of the search that led to the page, by a local keyword ranking of paragraphs; Chinese by two-character pieces.
  - When nothing matches well, the start of the page, as today.
  - It applies to long prose results: web pages, and stored pages read again. A result that is data (a table, JSON from a third-party tool) keeps its start.
  - A citation number still points at the page, and the passage is still found in the full stored text.
  - Accepted cost: the model reads a text with gaps; keyword matching misses a passage in other words; the change is in the delicate part of the run loop that stores and sends results; one new evaluation scenario, a detail deep in a long page.
- **Q9, 2026-11-26 (owner): three additions to the summary prompt; no styles.**
  - A gist first: one or two sentences that say what was learned.
  - The body is organized by concept, not by the order of the questions.
  - Key terms with a one-line definition each, when the conversation introduced terms; no list otherwise.
  - The user's note (Q4) also goes into the summary prompt.
  - Everything the prompt keeps today stays: open questions, uncertainty as it was, corrections, the title and the short name.
  - Accepted cost: about 60 input tokens per summary plus the note; only new summaries and new versions change, so a new version can be laid out differently from the one before; whether summaries read better is the owner's judgment.
  - Not chosen: optional summary styles.
- **Q10, 2026-11-26 (owner): the existing evaluation is extended; quality is also judged from the owner's daily use.**
  - About eight new scenarios with fictional facts: a branch on a jargon term; a branch on a whole paragraph; "simpler please"; a question with two readings; a question on a topic that has a summary; a follow-up in the "开" (On) web setting; a detail deep in a long page; a summary of a conversation that jumps between ideas.
  - Mechanical checks, which also run with the fake model: a branch answer does not restate the parent answer; a summary has the gist and, where terms exist, the list; no number is written for an attachment that has none.
  - A side-by-side report of two prompt versions per scenario. The owner's reading decides.
  - With a flag, a judge model adds two hints: which of two answers is clearer (asked twice, positions swapped), and whether each cited sentence is supported by the passage it cites. Evaluation only; a hint, never a pass or fail.
  - Feedback from the owner's daily use counts beside the evaluation.
  - **Cost and time** are part of the evaluation (owner, 2026-11-26):
    - Per scenario and in total, for both versions with the difference: input tokens, output tokens, model requests, tool calls, seconds to the first text, seconds in all. The app already records all of these per request; the report shows tokens and total seconds today and gains the time to the first text.
    - The fixed size of what Q1–Q9 add (answer prompt, branch paragraph, note, summary titles, tool section) is counted with the app's own estimator in the ordinary tests, with a limit for each, so growth is caught without a paid run.
    - Money: tokens times the prices given in the evaluation's configuration; without prices, tokens only.
    - Time from one run is one sample and moves with the service's load. Output tokens and the number of requests are the steady signs; a time difference counts only when it is large or repeats.
    - The judge's own tokens are reported apart from the answers under test.
  - Accepted cost: a full run grows from about 12 answers under test to about 20, and with the judge about 30 more requests; every real run is paid and within a budget the owner gives; a judge is noisy; eight scenarios to keep working.

### Out of scope

- Self-check cards and a guide-me mode, in any form (Q3).
- Chips with follow-up suggestions under an answer (Q5, part B). What the model writes in its own answer text is not covered by this line.
- A clarification card that pauses a run (Q6b).
- A model request that rewrites a follow-up question (Q7).
- Facts about the user extracted by the model (Q4).
- Optional summary styles (Q9).
- A default self-critique pass, long report-style answers, and an interview about the user's level ("Considered and not proposed").
- Showing on screen which summary titles were sent with an answer.
- Recording the prompt version with each request (it needs a migration).
- Search by meaning (the ask-anywhere session) and unresolved marks (the return-and-review session).

### Failure cases

| Case | What happens |
| --- | --- |
| The summary search fails or finds nothing above the threshold | The answer is made without the titles line; no error is shown |
| The note is empty | No note section in the prompt |
| The note is longer than the limit | Settings does not accept the extra text; nothing is cut silently |
| No passage of a long page matches | The start of the page, as today |
| A long result is data, not prose | Its start, as today |
| The model ignores the answer prompt | The answer is still an answer; the evaluation and the owner's reading show it |
| The answer prompt, note and titles leave too little room | They count in the context budget like the run prompt; the titles line is dropped first, then earlier turns are compressed as today |

### Acceptance scenarios

1. A plain question without tools: the request starts with the answer prompt; the answer gives the core idea first and defines a term at first use.
2. "Just the number" or "rewrite this sentence": the answer does only that, with no added explanation.
3. A branch on a term: the answer explains the term in the context of the passage and does not answer the parent question again.
4. A later question in the same branch: the request still carries the branch paragraph.
5. No chips with suggested questions appear under an answer. (How an answer's own text may end is open, Q5 part C.)
6. A question with two readings: the answer says which reading it takes and names the other.
7. A note in Settings is sent with the next answer and the next summary; clearing it removes it from the next request.
8. A question on a topic that has a summary: its title is in the request; with no matching summary there is no titles line.
9. A short follow-up ("What about the second one?"): the summary search and the pre-search both use the title and the selection with the question.
10. A run stops searching once the material answers the question, and names an attachment without a number.
11. A page of 10,000 words with the answer near the end: the first result the model gets contains that passage.
12. A new summary starts with a gist, is grouped by concept and lists the key terms; an existing summary is unchanged until a new version is made.
13. The evaluation report shows two prompt versions side by side per scenario, with tokens, requests, time to the first text and total time for each.
14. No screen offers a test, a quiz or a guided mode.

### Increments

| # | Content | The owner sees |
| --- | --- | --- |
| 1 | **Prompts and the yardstick.** The answer prompt with the branch paragraph and the line on two readings (Q1, Q2, Q6b); the tool section, run prompt version 4 (Q6); the summary prompt (Q9, without the note); the evaluation: new scenarios, checks, the side-by-side report, cost and time, size limits, the optional judge (Q10); the fix for attachment digests cut by the output limit. No change on screen. | A trial build to read answers and summaries in; one evaluation report, if a budget is given |
| 2 | **The note and the summary titles** (Q4), with the follow-up rule for the search (Q7); the note in the summary prompt (Q9). | A screenshot of the Settings field, then a build |
| 3 | **Best-matching passages of a long page** (Q8). | Nothing on screen; the evaluation scenario |

**Q11, 2026-11-26 (owner): prompts and the yardstick first, then the rest in this order.** Four increments were agreed; the one for follow-up suggestions left with Q5, so three remain.

Each increment is a task branch of its own and merges after the full test suite passes. Requirement and architecture text for an increment is a separate commit the owner confirms.

### Boundaries with the other design sessions (proposed, for the owner to settle)

- **Return and review** owns unresolved marks and the summary network. The "questions still open" part of a summary stays plain text in the body here; a format for marks is theirs to ask for.
- **Ask from anywhere and local AI** owns local models and search by meaning. Ideas 3 and 7 use the keyword search that exists; if search by meaning arrives, they use it through the same call and need no change here.
- **Sharing** owns the exchange format. Nothing decided here adds content to an answer or a summary that a shared page would have to carry; the user's note is not shared.

### The draft checked against the code (2026-11-25, `main` at f9edc39, 0.6.11)

Every line of "Where the code stands" still holds:

- An answer without tools has no system turn: `ConversationService` counts instructions only when the answer is a run, and `RunPrompt` says "An answer without tools gets none of this".
- The run prompt (`Prompts/en/run.md`, `Prompts/zh/run.md`, version 3) has six points: instructions and material, the tools, when not to call, when to stop, numbers for citations, no process notes. Nothing on how to explain or on branches.
- A branch tells the model about the selection only through the first question, which carries the quoted passage (`BranchContext.QuotedQuestion`).
- Recommended questions exist per workspace only (`Prompts/*/recommendations.md`, requirements §10.3.1).
- The pre-search query is still built by the L9 rule (`WebRun`, a list of referring words), with no model request.
- A long tool result is cut to its first 2,000 estimated tokens plus a handle (`AnswerRunner.Run.cs`). Since 0.6.10 the rest can be read in any conversation, but the model still gets the start first, not the part that matters.
- The evaluation set has the same 12 scenarios, all about mechanics.
- The summary prompt (`Prompts/*/summary.md`) asks for background, main explanations, open questions and uncertainty; it says nothing about organizing by concept or about key terms.
- Both to-dos in section 8 of agent-loop.md are open: attachment digests cut by the output limit on reasoning models, and the dropped line "name the attachment when relying on it".

## Goal

The answers a user gets to questions, and the documents they get from summaries, are better. The user takes fewer steps to get them.

## Where the code stands (2026-11-22)

- **Answers without tools have no system prompt.** The run prompt is added only when an answer is a run (`AnswerRunner.Run.cs`). Everyday explanations depend on each model's own habits.
- The run prompt covers tool mechanics, stopping and citations. It says nothing about how to explain, what level to aim at, or what a branch answer should do with the selected passage.
- Next-question ideas exist only for a whole workspace, not per answer.
- A follow-up question is rewritten for search by a rule (L9), not by the model.
- `fetch_page` gives the model the start of a page, not the part that matters for the question.
- The evaluation set checks mechanics. Quality is judged by the owner reading the answers.

## Ideas, best gain for the cost first

This table and the sections after it are the first draft of 2026-11-22, kept as background. Where they differ from "Discussion", the decisions there stand.

| # | Idea | First step | Extra cost | Borrowed from |
| --- | --- | --- | --- | --- |
| 1 | **An answer prompt for every answer.** Core idea first, then build up; one example or analogy; define a term at first use; length scaled to the question. A branch rule: explain the selected passage in its context, do not answer the parent question again, stay shorter than the parent. | Write the prompt in both languages, add it to answers without tools, and make the run prompt "answer prompt + tool section". Version it. | About 300 input tokens | OpenAI study mode, LearnLM, Morphic |
| 2 | **Follow-up suggestions per answer.** Two or three, written in the same response, with different intents (go deeper, act, go wider). Each names the phrase in the answer it extends, so the chip offers "branch from this phrase". Clicking fills the input box; nothing is sent by itself. | Ask for a fenced block at the end of the answer; parse it, strip it, show chips. Add a check to the evaluation set. | About 100 output tokens | Morphic, Co-STORM |
| 3 | **Relevant passages from fetched pages.** When a page is long, return the passages that best match the question, plus the handle. | Rank paragraphs with the existing full-text search; handle Chinese with bigrams. | Local only | GPT Researcher |
| 4 | **Standalone rewrite of follow-ups.** Only when the L9 rule fires, one small request rewrites the question so it stands alone. | Use it for the pre-search and show it in the run prompt. Measure on the follow-up scenario. | One cheap request | Perplexica, Open WebUI |
| 5 | **Stop and refine rules in the run prompt.** Stop at about three relevant sources or when two searches repeat; refine queries with terms found; plain words, no search operators; prefer primary sources. | Add the lines to the run prompt and run the evaluation again. | None | Open Deep Research, GPT Researcher |
| 6 | **Citation support check in evaluation.** For each sentence with a citation, a judge says whether the cited passage supports it. | A judge step in the run evaluation that reports the supported share. | One judge request per answer, in evaluation only | RAGAS, ALCE |
| 7 | **"What you already know here."** A short line with the titles of the user's own summaries on this topic, and "build on these, do not explain them again". | Put the top summary search hits into the answer prompt. | One local search | Mollick tutor prompt, DeepTutor |
| 8 | **Clarification card.** When a question has two clearly different readings, the model asks once, with two to four options and free text. | A tool with no executor that reuses the approval-card pause. | None unless it fires | Morphic, DeepTutor, Open Deep Research |
| 9 | **Explanation scenarios and a pairwise judge.** Scenarios such as a branch on a jargon term, or "simpler please". A judge compares two prompt versions for clarity, with positions swapped. | Four scenarios with fictional facts and the judge. | Two judge requests per scenario | LearnLM, Morphic |
| 10 | **Better summaries.** Organized by concept, not by the order things were asked; key terms with one-line definitions; optional styles. | One line in the summary prompt; compare three real summaries before and after. | None | STORM, Open Notebook |

## Considered and not proposed

- **A default self-critique pass.** It costs two to three times the requests. The citation check and the stop rules give most of the value.
- **Long, heavily cited "report" answers** in the style of Perplexica's writer prompt. A poor fit for curiosity-driven explanation.
- **An interview about the user's level.** The summaries the user already has say more, with no questions asked.

## Memory, lightest first

1. A line the user edits themself: level, language, preferences. No model call.
2. A note derived from the user's summaries, with links back to them, fed into the answer prompt (idea 7).
3. Facts extracted by the model, each tied to evidence (DeepTutor, mem0). Only if 1 and 2 prove too little.

## Not verified

- Every adaptation and cost above is an estimate.
- The study-mode prompts of commercial assistants are not published; only their public descriptions were used.
- LobeChat, Cherry Studio, Jan, AnythingLLM, SurfSense and Smart Connections were checked for existence and licence only.
- The papers on chain-of-verification and self-refine, and the ALCE method, were cited from memory.

## Sources

- OpenAI study mode: <https://openai.com/index/chatgpt-study-mode/>
- LearnLM: <https://arxiv.org/html/2412.16429v3>
- Mollick tutor prompt: <https://github.com/microsoft/prompts-for-edu/blob/main/Students/Prompts/Tutor.MD>
- Morphic prompts: <https://github.com/miurla/morphic/blob/main/lib/agents/prompts/search-mode-prompts.ts>; follow-up suggestions in `lib/render/prompt.ts`; the question tool in `lib/tools/question.ts`; evaluators in `evals/`
- Perplexica: <https://github.com/ItzCrazyKns/Perplexica/blob/master/src/lib/prompts/search/classifier.ts>, and `writer.ts`, `researcher.ts` beside it
- Open WebUI: query and follow-up templates in `backend/open_webui/config.py`
- Open Deep Research: <https://github.com/langchain-ai/open_deep_research/blob/main/src/open_deep_research/prompts.py>
- GPT Researcher: `gpt_researcher/context/lexical.py`, `compression.py`, `actions/query_processing.py`, `prompts.py`
- STORM and Co-STORM: `knowledge_storm/storm_wiki/modules/`, `knowledge_storm/collaborative_storm/modules/`
- DeepTutor: `deeptutor/capabilities/ask_questions/`, `capabilities/mastery/`, `services/memory/consolidator/`
- Open Notebook: `prompts/ask/`, default transformations in its migrations
- Khoj: `src/khoj/processor/conversation/prompts.py`
- RAGAS faithfulness: <https://github.com/explodinggradients/ragas/blob/main/docs/concepts/metrics/available_metrics/faithfulness.md>
- Tutor evaluation benchmark: arXiv 2412.09416
