# Free trial

**Status:** discussed with the owner, 2026-11-22. The owner judges the plan feasible. Not yet adopted: there is no entry in [evolution.md](../evolution.md) and no design note. The open decisions are listed near the end, each with a proposed default.

**Related:** [architecture §7.4](../architecture.md) records a built-in free experience as a long-term vision and sets three rules that this draft keeps: the server key is not in the installer, an exhausted quota does not delete local content, and the app does not switch to the user's paid configuration by itself.

## Goal

A new user installs Mindweave and asks a first question without getting or entering an API key. Users everywhere should be able to use it, including mainland China.

## Design in one paragraph

A small relay on the owner's VPS in Singapore holds the provider key and forwards Chat Completions requests to one cheap model. On first start the app gets an anonymous install token from the relay. The relay counts use per install and in total, and stops at a fixed daily cap. To the app, the relay is an ordinary service connection: the existing client code talks to it unchanged.

## Why the installer needs no secret

The installer contains only the relay's public address. The only secret is the provider key, and it exists only on the VPS. Trust comes from a token the relay hands out at first start.

The relay address cannot be hidden. Anything in a desktop app can be read out with a string search, a debugger or a traffic capture. So the design assumes an attacker knows everything except the provider key. Someone who reads the source gets exactly what an ordinary user gets: one small trial per enrollment, which costs proof-of-work and is capped per IP address. The global daily cap bounds the total however many addresses they use. Embedded secrets, obfuscation and request signing with a key in the client are not used, because they are read out the same way.

## First start

1. The app finds no configured connection and offers "start free trial" beside "use my own API".
2. It calls `GET /v1/enroll/challenge` and gets a random nonce and a difficulty.
3. It solves a proof-of-work of about 5 seconds and shows a short "preparing your trial" note.
4. It sends the solution to `POST /v1/enroll`.
5. The relay checks the solution, the per-IP limit and the kill switch. It creates an install row and returns a random 32-byte token. The relay keeps only a hash of the token.
6. The app writes the token to Keychain or Credential Manager, the same way it stores an API key. It saves a connection: the relay address, the model `mindweave-trial` (an alias the relay maps to the real model), and a credential reference to the token.
7. From then on the app uses its normal request path.

## One request

1. `GenerationRequests` accepts the task and freezes a `ModelSnapshot`: relay address, `ChatProtocol.ChatCompletions`, the token's credential reference. `ReadKeyAsync` reads the token.
2. `ChatCompletionsClient` sends `POST {relay}/v1/chat/completions` with `Authorization: Bearer <token>`, `stream: true`, `stream_options.include_usage`, and a header with the request's purpose (answer, title, summary, compression).
3. The relay checks, in order: the token hash, expiry, the kill switch, the per-minute limit, at most two at a time, the daily limit, the trial balance and the global cap. Near the end of an install's trial it refuses background purposes first. The first failed check answers at once.
4. The relay rewrites the body. It keeps `messages`, `stream` and `stream_options`. It sets the real model, caps `max_tokens`, turns thinking off and drops every other field. It estimates input tokens from the body size and refuses anything over the input limit.
5. In one SQLite transaction it holds back the worst-case cost against the install and against the global day.
6. It calls the provider over HTTPS with the server key.
7. It writes each stream event to the app as soon as it arrives. It reads only the last event, which carries the usage numbers.
8. When the stream ends it computes the real cost from the usage numbers (cache hit or miss, output, peak or off-peak) and charges that in place of the held amount.
9. After the answer, the app calls `GET /v1/quota` to update "left today" and "trial left".

**Edge cases.**

- The user stops the answer or closes the app: the relay sees the disconnect, cancels the provider call, and charges the partial usage, or the held amount if no usage came.
- Trial used up: 402 with `{"error":{"code":"trial_exhausted"}}`.
- Rate limited: 429 with `Retry-After`.
- Global cap reached for the day: its own error code. The app says the free trial is full today and points to adding one's own API.
- The owner's provider balance is empty: 503 "trial service unavailable", never 402, so the app does not tell the user their own trial is used up.
- Timeouts: the client allows 90 seconds to the first byte and 120 seconds between events. These fit as long as the proxy in front of the relay does not buffer.

## Starting limits

Conservative. All are relay settings, and the relay reports them through `/v1/quota`, so the app hard-codes none of them.

| Limit | Value |
| --- | --- |
| Trial per install | About $0.30 in total, roughly 100 to 150 answers; no renewal |
| Per install per day | 30 requests |
| Per install per minute | 5 requests; at most 2 at a time, the app's own two slots |
| Request shape | Fixed model, thinking off, input at most 32k tokens, output at most 2k tokens |
| Tools and web search | Off in the trial at first |
| Enrollment | Proof-of-work of about 5 seconds; 2 per IP address per day; IPv6 counted per /64 |
| Token lifetime | 30 days after first use |
| Global cap | $3 a day; first provider top-up $20 |
| Kill switches | One stops new enrollments, one stops the whole trial |
| Alert | An email when a day's spend passes half the cap |

**If abuse shows up:** refuse enrollment from data-center address ranges (public ASN lists); raise the proof-of-work difficulty; lower the global cap; block single installs by token. A light account is the last step, and only enrollment changes.

**Worst case:** farmers use up one day's cap early, and honest users see "full today". The loss is at most that day's cap. The prepaid provider balance is the hard ceiling over everything, whatever the code does.

## Hosting

- **Where.** The owner's existing VPS in Singapore. One relay serves everyone. Singapore is normally reachable from mainland China and abroad and needs no ICP filing. Connection quality from some mainland carriers at peak hours varies: to test from a mainland network before launch.
- **Second region.** The app holds a list of relay addresses, not one. A second host, if ever needed, is a new list entry and the same relay program.
- **What it runs.** An ASP.NET Core minimal API in C#, so the project keeps one language, run as a single binary under systemd. SQLite holds installs and counters. Caddy in front provides TLS with automatic certificates, with buffering off (`flush_interval -1`).
- **A domain name** points at the VPS, so the host can change without an app release.
- **Server key** in a file readable only by the service account, or an environment variable; never in the repository.
- **Logs** keep per-install counts and errors, never request or answer content.
- **Size.** The relay waits on the network and computes almost nothing. Each streaming request is one inbound and one outbound connection, a few tens of KB of memory and a few KB to a few tens of KB of text. The $3 cap is about 1,000 to 1,500 requests a day, with perhaps 10 to 50 streaming at once at peak. 1 vCPU and 512 MB to 1 GB of free memory is plenty; the relay process uses about 60 to 100 MB. The budget runs out long before the server does.

## Changes in the app

Checked in the code on 2026-11-22:

- The trial is a normal connection: `ModelSnapshot` with the relay address, `ChatProtocol.ChatCompletions`, and a credential reference to the install token. `ChatCompletionsClient` sends the token as `Bearer` unchanged. No new protocol adapter.
- The client already asks for usage numbers in the stream (`stream_options`), and already maps 402 and 429 to `GenerationErrorKind.QuotaOrRateLimited`.
- Request purposes already exist (`RequestPurpose`, `GenerationTaskKind`), so the purpose header needs no new concept.

New pieces:

- A trial connection kind in `ModelSettingsService`: a connection the user did not type in, and a clean switch to "my own API". This is the largest client piece.
- Enrollment with proof-of-work, and storing the token.
- Error codes that tell "trial used up", "full today" and "rate limited" apart.
- A remaining-quota display.
- A first-run choice between the free trial and one's own API.
- A capability preset for the relay address in `ModelCapabilityPresets`.
- A trial context budget that matches the relay's input limit, so compression starts before the relay would refuse.
- A privacy line: content passes through the relay to the provider, and the relay does not log it (requirements §9.4).

## When the quota runs out

Local content stays. The conversation shows a note with a button to the normal connection setup. Queued tasks fail with the same clear reason. The app never switches service by itself.

## Provider terms

The owner is an individual developer using a low-cost model. Three points to read in DeepSeek's terms before launch:

1. Whether offering free access to others inside one's own app is allowed. API terms usually allow building an app and forbid reselling the key.
2. The API customer answers for what goes through the key. The per-install counters let the owner block one install without stopping the trial.
3. Whether the owner's account type and payment method are accepted.

Mainland China also has rules for generative AI services offered to the public there. Hosting outside the mainland and using a licensed provider's API is the usual path for small projects. To read once before launch.

## Cost

Prices as read from the official pages on 2026-11-22; to read again before launch.

- **Model.** `deepseek-flash`: $0.30 per million input tokens and $1.20 per million output tokens at peak, half that off-peak; cached input $0.006 per million. With an assumed turn of 4,000 tokens in and 800 out, a turn costs about $0.002, or about $0.003 with background work.
- **Hosting.** The existing VPS; no new cost.
- **Running cost.** At most the global cap: $3 a day, about $90 a month.

Thinking mode is off for the trial, because reasoning tokens are billed as output.

## Development cost

Estimates in agent working days.

| Piece | Work | Days |
| --- | --- | --- |
| Relay | Enroll with proof-of-work, forward with hold and settle, usage parsing, limits, global cap, quota endpoint, purpose header, input limit, kill switches, logs without content, tests | 2 to 3 |
| App | The pieces listed under "Changes in the app" | 3 to 5 |
| Documents | Requirements §9.4 and architecture §7.4, a separate commit the owner confirms | 0.5 |
| End-to-end check | Real relay and real provider on a small budget, on macOS and Windows | 1 |

About 7 to 10 days in all, or 2 to 3 weeks with the owner's review rounds. The owner's own part: the provider account and top-up, the domain, server setup on the VPS, and two or three rounds of UI review. Running work afterwards is a few hours a month: watching spend, rotating the key, following model renames.

## Alternatives

1. **Light account** (email link or GitHub sign-in) in place of install tokens. A stronger limit per person, at the price of a sign-up step. Only enrollment changes.
2. **Cloudflare Workers with Durable Objects** in place of the VPS. $5 a month and no server to run, but reachability from mainland China is doubtful.
3. **No relay:** a local model as the zero-setup default, or a provider sign-in flow that hands the app a user-owned key. These reach the same goal by another road and can exist beside the relay.

## Not verified

- The token counts per turn are assumed.
- Whether the relay can turn thinking off for `deepseek-flash` through a request parameter.
- Reachability of the Singapore VPS from mainland carriers.
- DeepSeek's terms and the mainland rules (see "Provider terms").
- Nothing has been built or run.

## Open decisions

Each has a proposed default; none blocks the next step.

| Decision | Proposed default |
| --- | --- |
| Adopt the direction and schedule it | Owner's call; adoption moves this into evolution.md and a design note |
| Budget | $3 a day global cap, $20 first top-up |
| Trial size and renewal | About $0.30 per install, no renewal |
| Tools and web search in the trial | Off at first |
| Builds from source | Only Release builds carry the relay address; other builds have it empty, so forks do not draw on the owner's relay by default |
| Wording of the first-run choice and the privacy line | Settled in the UI review |

## Smallest next step

The relay with enroll, forward and the two counters on the Singapore VPS, and a "built-in trial" connection in the app behind a switch. About two to three days of work, tried with the owner's own key and a small prepaid balance.

## Sources

[DeepSeek pricing](https://api-docs.deepseek.com/quick_start/pricing), [Workers pricing](https://developers.cloudflare.com/workers/platform/pricing/), [Workers limits](https://developers.cloudflare.com/workers/platform/limits), [KV limits](https://developers.cloudflare.com/kv/platform/limits)
