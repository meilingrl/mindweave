# Mindweave

An AI conversation and exploration tool for everyday personal curiosity and for non-specialist study of a topic.

**Current status · 2026-11-16:** The first-release scope (S0–S7) and the post-first-release increments of requirements v0.13 (recommended questions and recommended nodes, summary links) are implemented. macOS is the main platform for development and use. Windows 11 x64 is the secondary platform, for support and demos.

**Repository:** [private GitHub repository](https://github.com/meilingrl/mindweave). For how to build, run and test, see the [run guide](docs/design/run-guide.md).

## Suggested reading order

| Entry | Purpose and scope |
| --- | --- |
| 1. [Product requirements and first-release scope](docs/requirements.md) | Current product behavior, core flows, the boundary between the first release and later work, and acceptance; answers "what to build" |
| 2. [Architecture and technical design](docs/architecture.md) | Current engineering responsibilities, navigation, data and lifecycles, summary network saving, verification status and implementation to-dos; answers "how to build it" |
| 3. [Run guide](docs/design/run-guide.md) | Building, testing, running, the data directory and releases |
| [Evolution directions and engineering guidance](docs/evolution.md) | Seven extension directions after the first release, the enabling seams and how work moves forward; directional guidance, not current requirements; adoption status is at the end |
| [Design history and historical records](docs/history/design-history.md) | Comparison of old routes, stage stopping points, prototype acceptance, and where review decisions came from; read as needed, not a current to-do list |
| [Detailed-design readiness review](docs/reviews/claude-design-readiness-20261024-172436/README.md) | Independent review of 2026-10-24: issue grading, versions v1–v4, the record of the user's decisions, and the [detailed-design handoff](docs/reviews/claude-design-readiness-20261024-172436/detailed-design-handoff.md); v4 has been written back into the current requirements and architecture, and the handoff is not an authorization to develop |
| [Independent design review](docs/reviews/review-20261023-023314-sgt/README.md) | Continues the layered navigation: the frozen five rounds of reports, versions, and the sources of the D1–D8 discussions; adjustments confirmed later follow the current requirements and architecture |

Notes on the migration of the review materials and on old paths are in `docs/reviews/README.md` in the repository. Product code is in `src/`, tests are in `tests/`, and build and packaging scripts are in `scripts/`; prototypes are kept separately.

Requirements and architecture are each maintained by their own responsibility and avoid copying each other's full details. Historical records keep the conclusions of their time; an old "to be decided" does not override a later decision. When a conflict is found, go back to the explicit decision, and do not widen the scope on your own.

## Verification assets

Only the entry points are listed here, not their internal lists. Usage, versions and the limits of their evidence follow each one's own README.

| Entry | Purpose |
| --- | --- |
| [Avalonia chat prototype](prototypes/avalonia-chat/README.md) | Reference for chat visuals, branching from a selection, returning to the branch source, simulated streaming, and recovery |
| [LiveMarkdown component sample](prototypes/livemarkdown-chat/README.md) | Independent verification of selections across paragraphs, anchor checking, re-layout, and appending |
| [API compatibility probe](prototypes/responses-compat/README.md) | Record of small-scale protocol checks for Responses, Chat Completions and Messages; stores no keys |
| [Summary network interaction sample](prototypes/network-layout/README.md) | This round of verification is finished; kept as an interaction reference for layout browsing, drag pulling, and hover attraction |

A prototype passing does not mean that the official product, all platforms, or the course acceptance has passed, and it does not prove the value of delayed review. The samples are not being extended for now, and the product is not developed directly along the samples by default.
