# Reply quality

**Status:** first draft, 2026-11-22. Not agreed. Based on a research pass of the same day over open-source projects: their prompts, code and documents were read; the adaptations to Mindweave and all cost estimates are inferences, not measurements.

**Related:** [agent-loop.md](../design/agent-loop.md) (run loop, L1–L12, section 8), [web-search.md](../design/web-search.md), [requirements §10.4](../requirements.md).

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

## Owner decisions first

[Requirements §2](../requirements.md) excludes assessing learning results. These two touch that line:

- **Test myself.** An action on a summary node that makes three to five self-check cards. Shown once, answers revealed by the user, nothing stored. (Matuschak's prompt guide, DeepTutor.)
- **Guide me.** A mode per conversation in which the model leads with questions and hints. No grades. (Mollick tutor prompt.)

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
- Matuschak, how to write good prompts: <https://andymatuschak.org/prompts/>
- Tutor evaluation benchmark: arXiv 2412.09416
