# Reply quality

**Status:** in discussion with the owner since 2026-11-25; decisions are recorded under "Discussion". Nothing outside that section is agreed. First draft 2026-11-22. Based on a research pass of the same day over open-source projects: their prompts, code and documents were read; the adaptations to Mindweave and all cost estimates are inferences, not measurements.

**Related:** [agent-loop.md](../design/agent-loop.md) (run loop, L1–L12, section 8), [web-search.md](../design/web-search.md), [requirements §10.4](../requirements.md).

## Discussion

Started 2026-11-25. The owner decides; each decision is recorded here with its date. Questions are asked one at a time, in the order of what depends on what.

### Order of the questions

| # | Question | Depends on |
| --- | --- | --- |
| Q1 | Does every answer get an answer prompt, and how strongly does it steer? (idea 1) | Nothing; the others hang on it |
| Q2 | The rule for branch answers (idea 1) | Q1 |
| Q3 | Self-check cards and a guide-me mode (dropped, see the decisions) | Q1 |
| Q4 | What the user already knows, and memory (idea 7, "Memory") | Q1 |
| Q5 | Follow-up suggestions per answer (idea 2) | Q1, Q2 |
| Q6 | The tool section: stop and refine rules, naming an attachment (idea 5; agent-loop §8) | Q1 (the run prompt becomes "answer prompt + tool section") |
| Q6b | The clarification card (idea 8) | Q1 |
| Q7 | Rewriting follow-up questions (idea 4; reopens L9, decided 2026-11-19) | Q6 |
| Q8 | Relevant passages from fetched pages (idea 3) | Nothing |
| Q9 | Better summaries (idea 10) | Nothing |
| Q10 | The evaluation of explanations and of citations (ideas 6, 9) | What Q1–Q9 keep |
| Q11 | Out of scope, failure cases, acceptance scenarios, the first increment | All |

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
- **Q3, 2026-11-25 (owner): no self-check cards and no guide-me mode.** Both are dropped outright, in any form: no card, no mode, no line in the answer prompt about quizzing or guiding, and no follow-up suggestion that offers a test. Requirements §5.4 stays as written.
- **Q4, 2026-11-25 (owner): the answer prompt carries a note the user writes and the titles of matching summaries. No extracted facts.**
  - The note: one free-text field in Settings, at most about 500 characters, one for all workspaces. It goes into every answer's prompt, marked as the user's own preferences. No model call; nothing is derived. It is not shared.
  - The titles: before each answer, one local keyword search of this workspace's summaries with the question and, in a branch, the selected passage. The top three titles above a match threshold go into the prompt with "build on these; do not explain them again from the start". Titles only; with tools on, the model can open one through the summary search.
  - Facts extracted by the model (level 3 of "Memory") are not built.
  - Accepted cost: up to about 200 input tokens for the note and about 50 for the titles; one stored preference; a Settings field whose look the owner confirms; a wrong match can make the model skip a needed explanation; nothing on screen shows which titles were sent; the Settings text on what is sent to the AI (requirements §9.4) gains a line.
- **Q5, 2026-11-25 (owner): two or three follow-up suggestions under each answer, written in the same response.**
  - The answer prompt asks for a small marked block at the end of the answer. The app parses it, removes it from the body and shows chips. No extra request.
  - Short questions with different intents: go deeper, apply it, go wider. None offers a test (Q3).
  - A suggestion can name a phrase in the answer; its chip then opens a branch from that phrase with the question filled in.
  - Clicking fills the input box. Nothing is sent by itself.
  - Shown under the latest answer of a conversation only. Stored with the answer, so they survive a restart.
  - Not part of the answer body: not in context, titles, summaries, export or sharing.
  - One setting turns them off.
  - A missing or broken block gives no chips and leaves the answer as it is.
  - Accepted cost: about 80 input and 100 output tokens per answer; the tail of a streaming answer is held back so the block never shows; a mangled block can leave a stray line in the body; a new kind of stored block (whether it needs a migration is checked at design time); the owner confirms the look of the chips.
  - Not chosen: a separate small request after each answer.

### Boundaries with the other design sessions (proposed, for the owner to settle)

- **Return and review** owns unresolved marks and the summary network. Follow-up suggestions (idea 2) are offered once under an answer and store nothing about what is unresolved; a suggestion the user does not take leaves no mark.
- **Ask from anywhere and local AI** owns local models and search by meaning. Ideas 3 and 7 use the keyword search that exists; if search by meaning arrives, they use it through the same call and need no change here.
- **Sharing** owns the exchange format. Whatever an answer gains here (suggestions, for example) is stated as "shared or not" in this draft before it is built.

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
