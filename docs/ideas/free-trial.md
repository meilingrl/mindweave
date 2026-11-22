# Free trial

**Status:** first draft, 2026-11-22. Not agreed. Taken from the opening brief of the owner's technical discussion of the same day; the owner's questions and answers from that discussion are still to be added.

**Related:** [architecture §7.4](../architecture.md) records a built-in free experience as a long-term vision and sets three rules that this draft keeps: the server key is not in the installer, an exhausted quota does not delete local content, and the app does not switch to the user's paid configuration by itself.

## Goal

A new user installs Mindweave and asks a first question without getting or entering an API key.

## Design in one paragraph

A small stateless relay holds the provider key and forwards Chat Completions requests to one cheap model. The app gets an anonymous install token on first start. The relay counts use per install and in total, and stops at a fixed daily cap. To the app, the relay is an ordinary service connection.

## Parts

| Part | How |
| --- | --- |
| Identity | On first start the app calls an enroll endpoint and gets a random install token. It is stored in Keychain or Credential Manager like an API key. There is no sign-up. Enrollments are capped per IP address per day; a proof-of-work challenge can be added later. |
| Metering | The relay sets the output limit itself. Before forwarding it holds back the worst-case cost; when the stream ends it reads the usage numbers and charges the real cost. |
| Background work | Titles, summaries and compression go through the same connection, so they are counted. The app sends the purpose of each request in a header. When an install has little left, the relay refuses background work first, so the user's own questions last longer. |
| Cost cap | Two layers: a global daily counter, after which every request is refused; and a prepaid provider balance, which is the absolute ceiling whatever the code does. |
| Streaming | The relay passes the provider's stream straight through and reads only the final usage lines. |
| Server key | A secret of the relay's hosting platform. |
| Counters | Storage with strong consistency (on Cloudflare: Durable Objects, one per install and one global). A plain key-value store is not suitable for counters. |

## Changes in the app

Checked in the code on 2026-11-22:

- The trial is a normal connection: relay address, `ChatProtocol.ChatCompletions`, and a credential reference that points to the install token. `ChatCompletionsClient` sends the token unchanged. No new protocol adapter.
- The client already asks for usage numbers in the stream, and already maps 402 and 429 to a quota-or-rate-limit failure.

New pieces:

- An error code that tells "trial used up" apart from "rate limited".
- An endpoint for the remaining quota.
- A first-run choice between the free trial and the user's own API.
- A capability preset for the relay address.
- A privacy line: content passes through the relay, and the relay does not log it.

## When the quota runs out

Local content stays. The conversation shows a note with a button to the normal connection setup.

## Cost

Prices as read from the official pages on 2026-11-22; they must be read again before any decision.

- **Model.** `deepseek-flash`: $0.30 per million input tokens and $1.20 per million output tokens at peak, half that off-peak. With an assumed turn of 4,000 tokens in and 800 out, a turn costs about $0.002, or about $0.003 with background work. 200 turns for one user is about $0.60.
- **Hosting.** Cloudflare Workers Paid: $5 a month.
- **Example.** A global cap of $10 a day allows roughly 3,000 to 5,000 turns a day. The worst case is $300 a month plus hosting.

Thinking mode should be off for the trial, because reasoning tokens are billed as output.

## Alternatives

1. **Light account** (email link or GitHub sign-in) in place of install tokens. A stronger limit per person, at the price of a sign-up step. Only enrollment changes.
2. **A C# minimal API on a small server**, so the project stays in one language. The owner then runs a server, and open streams use its resources.
3. **No relay:** a local model as the zero-setup default, or a provider sign-in flow that hands the app a user-owned key. These reach the same goal by another road and can exist beside the relay.

## Not verified

- The token counts per turn are assumed.
- Whether the relay can turn thinking off for the chosen model through a request parameter.
- Nothing has been built or run.

## Open questions

- The size of the trial per install, and whether it renews.
- The monthly budget the owner accepts.
- Which model, and whether the trial offers tools and web search (web search has its own cost).
- Who operates the relay for people who build Mindweave from source.
- The wording of the privacy line.

## Smallest next step

A relay with enroll, forward and the two counters, and a "built-in trial" connection in the app behind a switch. About a day of work, tried with the owner's own key and a small prepaid balance.

## Sources

[DeepSeek pricing](https://api-docs.deepseek.com/quick_start/pricing), [Workers pricing](https://developers.cloudflare.com/workers/platform/pricing/), [Workers limits](https://developers.cloudflare.com/workers/platform/limits), [KV limits](https://developers.cloudflare.com/kv/platform/limits)
