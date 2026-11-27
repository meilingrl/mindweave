# Model capabilities: research, presets and queries

Groundwork for direction 2 (agent platform): stage 1 (image attachments) and stage 2 (tool calls) both first need to know what the current model can do. E4 already has `ModelCapabilities` and the `model_config.capabilities` column ([evolution seams](evolution-seams.md#e4-model-capability-description)); this work makes it actually filled in, shown and queried. **No migration needed**: the column has existed since v3, and old rows read as the protocol default.

## 1. Conclusions

- **Features ask only one question.** `IModelSettings.ActiveModelHasAsync(ModelCapability, ct)`: whether the current model has a given capability (`ToolCalls`, `ImageInput`, `ReasoningOutput`, `PromptCaching`, `Streaming`). When no model is configured, and for any capability not declared, the answer is always no. A request already accepted no longer asks the current settings; it asks its frozen snapshot: `snapshot.Capabilities.Has(...)`. Core and Application still must not contain provider or model names (`SeamTests`).
- **Presets prefill by service and model.** `Desktop/ViewModels/Pages/ModelCapabilityPresets.Suggest(baseUrl, model)`; the rules are in §3. Unknown services (custom addresses, relays) and unknown models: all off.
- **设置 › 模型连接 › 高级设置 (Settings › Model connection › Advanced settings).** Collapsed by default; when expanded, the “模型能力” (model capabilities) row has two checkboxes, “工具调用” (tool calls) and “图片输入” (image input), with plain-language explanations. Changing the service or the model prefills again (the user's changes for the previous model are discarded with it); if the user changes them, they are saved as the user set them. The other three (streaming, reasoning content, prompt caching) are saved with the preset but not shown: no feature uses them yet, and showing them would only confuse.
- **The research read only official public documentation; no paid request was sent.** Model lineups change fast, and the name table will go out of date; §4 lists the items that need a real call to verify, and whether to pay for them is the user's decision.

## 2. Implementation

| Location | Content |
| --- | --- |
| Core `Models/ModelCapabilities.cs` | New enum `ModelCapability`; `ModelCapabilities.None` (all no), `Has(capability)`, `With(capability, on)` |
| Application `IModelSettings` / `ModelSettingsService` | `ActiveModelHasAsync`; `ModelSettingsInput.Capabilities` (nullable): capabilities declared by the form take precedence; when it is null, the old rule applies (the same model keeps its stored capabilities, a changed model returns to the protocol default). Context capacity always comes from the form's “上下文容量” (context capacity) field |
| Desktop `ModelCapabilityPresets` | Groups into families by preset address; model names match on the whole name (case-insensitive); within a family the first match wins |
| Desktop `ModelSettingsViewModel` | `IsAdvancedOpen`, `DeclaresToolCalls`, `DeclaresImageInput`; on load shows the stored capabilities; on save overlays the two checkboxes onto the preset (or stored) capabilities |
| `ModelSettingsForm.axaml` (hosted by `SettingsHomeView.axaml`) | The “高级设置” (advanced settings) collapse button at the end of the form, the “模型能力” row and the “输出上限” row |

**Output limit (2026-11-21, requirements v0.23 §9.6).** The “输出上限” (output limit) row under the capabilities: a token field (empty: “留空由服务决定”, leave empty and the service decides), a one-line hint, and an `IconInfo` whose tooltip gives the details (sent with every answer; reasoning usually counts; the same room is reserved in the context budget; internal requests keep their own limits). It fills the existing `model_config.max_output_tokens` column through `ModelSettingsInput.MaxOutputTokens`, so there is **no migration**. The form always sends what it shows, so an empty field clears a saved limit. The range is 256 to half the context capacity (declared, or the 32,000 default); otherwise the save is refused with `InvalidOutputLimit`. Summary, compression and naming requests override it with their own constants, as before.

**Old settings.** Settings saved before this work store the protocol default (streaming only), which does not represent the user's judgment of the model. So when the stored capabilities equal the protocol default, the form shows the preset suggestion and writes it on the next save; until then `ActiveModelHasAsync` still answers "no" for tools and images. The cost: if the user deliberately turned all capabilities off, they will see the preset suggestion when they open settings again (and must turn them off once more). Telling "never declared" apart from "declared all off" needs a marker in the JSON; not done for now.

**Not done.** Context capacity is not prefilled by model (compression behavior would change because of it; left for the user to decide); OpenRouter's `/models` and Ollama's `/api/show` can give each model's capabilities (§3.7, §3.9), but `ListModelsAsync` currently returns only names; wiring them in is later work.

## 3. Research by service (official documentation, accessed 2026-11-17)

The protocol is always Chat Completions (`POST {base}/chat/completions`); images means the `image_url` parts in the `content` array, and tools means `tools` / `tool_calls`. "To verify" means one real call is needed to confirm.

### 3.1 DeepSeek (`https://api.deepseek.com`)

| Model | Images | Tools | Reasoning content | Caching | Prefill |
| --- | --- | --- | --- | --- | --- |
| `deepseek-flash` (V4.1 Flash; the old name `deepseek-v4-flash*` still works) | Yes (URL or base64) | Yes | `reasoning_content`, on by default | Automatic | Tools, images, reasoning, caching |
| `deepseek-v4-pro` | No | Yes | Yes | Automatic | Tools, reasoning, caching |
| `deepseek-chat` / `deepseek-reasoner` | — | Yes | reasoner has it | Automatic | Tools (the docs say retired 2026-07-24; whether still usable is to verify) |

Note: with tools, later requests must send `reasoning_content` back unchanged, or they get 400; in thinking mode `tool_choice` cannot be required or a named function.
Sources: [Pricing and models](https://api-docs.deepseek.com/quick_start/pricing), [Chat API](https://api-docs.deepseek.com/api/create-chat-completion), [Thinking mode](https://api-docs.deepseek.com/guides/thinking_mode), [Caching](https://api-docs.deepseek.com/guides/kv_cache), [Change log](https://api-docs.deepseek.com/updates).

### 3.2 Kimi (Moonshot AI, `https://api.moonshot.cn/v1`)

| Model | Images | Tools | Reasoning content | Caching | Prefill |
| --- | --- | --- | --- | --- | --- |
| `kimi-k3*` (including `kimi-k3.1-pro`) | Yes, **only base64 or `ms://` file IDs, not public URLs** | Yes | Always on | Automatic | All |
| `kimi-k2*` | Not stated in the docs | Yes | Yes | Automatic | Tools, reasoning, caching |
| `moonshot-v1-*-vision-preview`, `kimi-latest` | No longer in the current docs | | | | Off |

K2 is planned to be retired at the end of December 2026. Multi-turn requests and tool calls must send back the complete assistant message.
Sources: [Pricing](https://platform.kimi.com/docs/pricing/chat), [Chat API](https://platform.kimi.com/docs/api/chat), [K3 quick start](https://platform.kimi.com/docs/guide/kimi-k3-quickstart.md), [Vision model](https://platform.kimi.ai/docs/guide/use-kimi-vision-model).

### 3.3 Zhipu GLM (`https://open.bigmodel.cn/api/paas/v4`)

| Model | Images | Tools | Prefill |
| --- | --- | --- | --- |
| Text `glm-4.5*` to `glm-5.*` | No | Yes (streamed tools need `tool_stream: true`) | Tools, reasoning, caching |
| Vision `glm-5.3-flash`, `glm-4.6v`, `autoglm-phone` | Yes | Yes | All |
| Vision `glm-5v-turbo`, `glm-4.6v-flash`, `glm-4.1v-thinking-*` | Yes | **No** | Images (4.1v-thinking adds reasoning), caching |
| `glm-4v-flash` | Only 1 image, **no base64** | No | Off (we send base64) |

`tool_choice` can only be `auto`. The docs for `glm-5.3-flashx` are inconsistent (text or vision); it is treated as text. Caching is implicit and automatic.
Sources: [Model overview](https://docs.bigmodel.cn/cn/guide/start/model-overview), [Chat completion](https://docs.bigmodel.cn/api-reference/模型-api/对话补全), [Caching](https://docs.bigmodel.cn/cn/guide/capabilities/cache).

### 3.4 OpenAI (`https://api.openai.com/v1`)

| Model | Images | Tools on Chat Completions | Prefill |
| --- | --- | --- | --- |
| `gpt-6*` (astra, sol, terra, luna) | Yes | **No**: the docs say function calling needs the Responses protocol (for luna, available only with `reasoning_effort: none`) | Images, caching |
| `gpt-4o*`, `gpt-4.1*` (retirement 2027-01-14), `gpt-5*` (retirement 2026-12-11) | Yes | Yes | Tools, images, caching |

Chat Completions does not return reasoning content; caching is automatic.
Sources: [Models](https://developers.openai.com/api/docs/models), [gpt-6.1-terra](https://developers.openai.com/api/docs/models/gpt-6.1-terra), [gpt-6.1-sol](https://developers.openai.com/api/docs/models/gpt-6.1-sol), [gpt-6-luna](https://developers.openai.com/api/docs/models/gpt-6-luna), [gpt-4.1](https://developers.openai.com/api/docs/models/gpt-4.1), [Reasoning](https://developers.openai.com/api/docs/guides/reasoning), [Prompt caching](https://developers.openai.com/api/docs/guides/prompt-caching), [Deprecations](https://developers.openai.com/api/docs/deprecations).

### 3.5 Claude through an OpenAI-compatible interface

- **Anthropic's own compatibility layer** (`https://api.anthropic.com/v1/`; the official description says it is mainly for testing and comparison and not recommended for production): supports both images and tools; `strict` is ignored; Opus 5.5, Sonnet 5.5 and others do not accept a `tool_choice` of required / a named function; **reasoning content and prompt caching are lost**; from Claude 4.7 on, `temperature` < 1 or a changed `top_p` gives 400. Prefill: `claude-*` → tools, images. Source: [OpenAI SDK compatibility](https://platform.claude.com/docs/en/api/openai-sdk). The layer covers chat completions only: the model list there is Anthropic's own API, which takes the key as `x-api-key` with an `anthropic-version` header and lists 20 models a page by default; the app asks it that way, for up to 1,000 (2026-11-28, checked against the real service).
- **Through OpenRouter**: images and tools are kept; reasoning content is returned when the `reasoning` parameter is on, and with tools `reasoning_details` must be sent back unchanged; caching is kept but is **not automatic**: it needs `cache_control`. Sources: [Prompt caching](https://openrouter.ai/docs/guides/best-practices/prompt-caching), [Reasoning tokens](https://openrouter.ai/docs/guides/best-practices/reasoning-tokens).
- **Other relays** (custom address): whether images and tools pass through depends on the relay itself; the preset is always off, and the user turns them on.

### 3.6 Alibaba Cloud Model Studio (`https://dashscope.aliyuncs.com/compatible-mode/v1`)

| Model | Images | Tools | Prefill |
| --- | --- | --- | --- |
| `qwen4-max*`, `qwen3.7-plus*` | Yes | Yes | All |
| `qwen3-vl-*` | Yes | Yes | All |
| `qwen-vl-*` and other older vision models | Yes | Not listed | Images, caching |
| max / plus / flash of `qwen4-*` and `qwen3.5`–`3.8` | No | Yes | Tools, reasoning, caching |
| `qwen-max／plus／flash／turbo*` | No | Yes | Tools, caching |
| Hosted `kimi-k3`, `glm-5.3`, `deepseek-v4-pro` | As the original vendor | As the original vendor | The original vendor's rules |

Thinking mode does not support `tool_choice: required`; the old compatibility page says "streaming cannot use tools", and the new function-calling page says the opposite (to verify). New workspace-specific addresses look like `https://{WorkspaceId}.cn-beijing.maas.aliyuncs.com/compatible-mode/v1`; they do not match the presets and are treated as unknown.
Sources: [Function calling](https://help.aliyun.com/zh/model-studio/user-guide/qwen-function-calling), [qwen4-max](https://help.aliyun.com/zh/model-studio/qwen4-max), [Deep thinking](https://help.aliyun.com/zh/model-studio/deep-thinking), [Vision](https://help.aliyun.com/zh/model-studio/vision), [Context cache](https://help.aliyun.com/zh/model-studio/user-guide/context-cache), [Model list](https://help.aliyun.com/zh/model-studio/models), [OpenAI compatibility](https://help.aliyun.com/zh/model-studio/compatibility-of-openai-with-dashscope), [Base URLs](https://help.aliyun.com/zh/model-studio/base-url).

### 3.7 OpenRouter (`https://openrouter.ai/api/v1`)

Each model in `GET /models` carries `architecture.input_modalities` (containing `image` means it can see images), `supported_parameters` (containing `tools`, `reasoning`) and `context_length`. This is the only source from which capabilities can be judged automatically and reliably; later work should switch to reading it. Current prefill: take the part after the slash in `vendor/model` and judge it by the original vendor's rules; caching is always off (it is not automatic for all vendors). Note: when none of a model's providers support tools, `tools` is **silently ignored**; when sending tools, the request should include `provider.require_parameters: true`.
Sources: [Models](https://openrouter.ai/docs/guides/overview/models), [List models API](https://openrouter.ai/docs/api-reference/list-available-models), [Provider selection](https://openrouter.ai/docs/guides/routing/provider-selection), [Tool calling](https://openrouter.ai/docs/guides/features/tool-calling).

### 3.8 SiliconFlow (`https://api.siliconflow.cn/v1`)

Supports `image_url` and `tools`, but which models support them is shown only in the VLM / Tools tags of the “模型广场” (model marketplace), and `/models` carries no capability information. Prefill is the same as for OpenRouter (the original vendor's rules, caching off); to verify.
Sources: [Chat API](https://docs.siliconflow.cn/cn/api-reference/chat-completions/chat-completions), [Function calling](https://docs.siliconflow.cn/en/userguide/guides/function-calling), [Model list](https://docs.siliconflow.com/en/api-reference/models/get-model-list.md).

### 3.9 Ollama (`http://localhost:11434/v1`)

The compatibility layer supports images (**base64 only**) and `tools` (`tool_choice` is not supported), but this depends on the models installed locally. The native `POST /api/show` returns `capabilities` (`vision`, `tools`, `thinking`). The current prefill is all off; later it can read `/api/show`. It runs locally and is free, so it can be verified directly.
Sources: [OpenAI compatibility](https://docs.ollama.com/api/openai-compatibility.md), [Context length](https://docs.ollama.com/context-length), [API](https://github.com/ollama/ollama/blob/main/docs/api.md).

## 4. To verify with real calls (paid; the user decides)

1. DeepSeek `deepseek-flash`: images and tools in the same request; whether `deepseek-chat` is still usable.
2. Kimi K3: base64 images.
3. OpenAI GPT-6 terra / luna: tool behavior on Chat Completions.
4. Model Studio: tool calls with `stream=true`.
5. SiliconFlow: whether inferring by vendor prefix is accurate.

Each needs only one or two very short requests. If none is done, the prefill still works; only a few models may be marked wrongly, and the user can change them in settings.

## 5. Conventions for stages 1 and 2 and agent-docs

- **UI location.** Capabilities are declared at **设置 › 模型连接 › 高级设置 › 模型能力**, and the checkbox labels are “工具调用” and “图片输入”.
- **Glossary.** `ModelCapabilities` and `ModelCapability` are in [architecture §2.3](../architecture.md#23-code-glossary).
- **How to use it.** Ask `ActiveModelHasAsync` before showing an attachment or tool entry point; when sending, ask the snapshot's `Capabilities.Has`; saving settings raises `ModelSettingsService.Changed`, which entry points can use to refresh.
- **Stage 1 (images)** always sends base64 data URLs: Kimi and Ollama do not accept public URLs. Stage 5's `AudioInput` needs a field added to `ModelCapabilities` (JSON column, no migration needed, old rows read as false).
- **Stage 2 (tools)** sends only `tool_choice: "auto"` (GLM, Ollama, DeepSeek / Qwen in thinking mode, and Claude 5.5 all reject other values); DeepSeek, Kimi, and Claude on OpenRouter need the reasoning content sent back unchanged in multi-turn requests with tools; on OpenRouter add `provider.require_parameters: true`.
