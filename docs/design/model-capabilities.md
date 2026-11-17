# 模型能力：调研、预设与查询

方向 2（智能体平台）的前置工作：阶段 1（图片附件）与阶段 2（工具调用）都要先知道当前模型能做什么。E4 已有 `ModelCapabilities` 与 `model_config.capabilities` 列（[演进接缝](evolution-seams.md#e4-模型能力描述)），本次让它真正被填写、显示和查询。**无需迁移**：列在 v3 已存在，旧行读作协议默认值。

## 1. 结论

- **功能只问一个问题。** `IModelSettings.ActiveModelHasAsync(ModelCapability, ct)`：当前模型是否具备某项能力（`ToolCalls`、`ImageInput`、`ReasoningOutput`、`PromptCaching`、`Streaming`）。没配置模型时、未声明的能力一律为否。已接受的请求不再问当前设置，而问它冻结的快照：`snapshot.Capabilities.Has(...)`。Core 与 Application 仍不得出现供应商或模型名（`SeamTests`）。
- **预设按服务与模型预填。** `Desktop/ViewModels/Pages/ModelCapabilityPresets.Suggest(baseUrl, model)`，规则见 §3。未知服务（自定义地址、中转）与未知模型：全部关闭。
- **设置 › 模型连接 › 高级设置。** 默认收起；展开后“模型能力”一行有“工具调用”“图片输入”两个勾选框和白话说明。换服务或换模型时重新预填（用户对上一个模型的改动随之作废）；用户改了就按用户的保存。其余三项（流式、思考内容、提示缓存）随预设保存但不显示：目前没有功能用到，显示只会让人困惑。
- **调研只读官方公开文档，未发任何付费请求。** 模型阵容变化很快，名称表会过时；§4 列出需要真实调用核实的项，是否付费由你决定。

## 2. 实现

| 位置 | 内容 |
| --- | --- |
| Core `Models/ModelCapabilities.cs` | 新增枚举 `ModelCapability`；`ModelCapabilities.None`（全否）、`Has(capability)`、`With(capability, on)` |
| Application `IModelSettings` / `ModelSettingsService` | `ActiveModelHasAsync`；`ModelSettingsInput.Capabilities`（可空）：表单声明的能力优先；为空时沿用旧规则（同一模型保留已存能力，换模型回到协议默认）。上下文容量始终取表单的“上下文容量”栏 |
| Desktop `ModelCapabilityPresets` | 按预设地址分族，模型名整名匹配（不分大小写），族内第一条命中 |
| Desktop `ModelSettingsViewModel` | `IsAdvancedOpen`、`DeclaresToolCalls`、`DeclaresImageInput`；载入时显示已存能力；保存时把两项勾选叠加到预设（或已存）能力上 |
| `SettingsHomeView.axaml` | 卡片末尾的“高级设置”折叠按钮与“模型能力”一行 |

**旧设置。** 本次之前保存的设置存的是协议默认值（只有流式），它不代表用户对模型的判断。所以已存能力等于协议默认值时，表单显示预设建议，下次保存时写入；在那之前 `ActiveModelHasAsync` 对工具与图片仍答“否”。代价：用户若特意把所有能力都关掉，再打开设置时会看到预设建议（需再关一次）。要区分“从未声明”与“声明为全关”须在 JSON 里加标记，暂不做。

**未做。** 上下文容量没有按模型预填（压缩行为会因此改变，留给你决定）；OpenRouter 的 `/models` 与 Ollama 的 `/api/show` 能给出每个模型的能力（§3.7、§3.9），但 `ListModelsAsync` 目前只返回名称，接入它们是后续工作。

## 3. 各服务调研（官方文档，2026-11-17 访问）

协议均为 Chat Completions（`POST {base}/chat/completions`），图片指 `content` 数组里的 `image_url` 部分，工具指 `tools` / `tool_calls`。“待核实”表示需要一次真实调用确认。

### 3.1 DeepSeek（`https://api.deepseek.com`）

| 模型 | 图片 | 工具 | 思考内容 | 缓存 | 预填 |
| --- | --- | --- | --- | --- | --- |
| `deepseek-flash`（V4.1 Flash；旧名 `deepseek-v4-flash*` 仍可用） | 是（URL 或 base64） | 是 | `reasoning_content`，默认开 | 自动 | 工具、图片、思考、缓存 |
| `deepseek-v4-pro` | 否 | 是 | 是 | 自动 | 工具、思考、缓存 |
| `deepseek-chat` / `deepseek-reasoner` | — | 是 | reasoner 有 | 自动 | 工具（文档称 2026-07-24 停用，是否仍可用待核实） |

注意：带工具时，后续请求必须原样带回 `reasoning_content`，否则 400；思考模式下 `tool_choice` 不能是 required 或指定函数。
来源：[价格与模型](https://api-docs.deepseek.com/quick_start/pricing)、[对话接口](https://api-docs.deepseek.com/api/create-chat-completion)、[思考模式](https://api-docs.deepseek.com/guides/thinking_mode)、[缓存](https://api-docs.deepseek.com/guides/kv_cache)、[更新记录](https://api-docs.deepseek.com/updates)。

### 3.2 Kimi（月之暗面，`https://api.moonshot.cn/v1`）

| 模型 | 图片 | 工具 | 思考内容 | 缓存 | 预填 |
| --- | --- | --- | --- | --- | --- |
| `kimi-k3*`（含 `kimi-k3.1-pro`） | 是，**只收 base64 或 `ms://` 文件 ID，不收公网 URL** | 是 | 始终开 | 自动 | 全部 |
| `kimi-k2*` | 文档未写 | 是 | 是 | 自动 | 工具、思考、缓存 |
| `moonshot-v1-*-vision-preview`、`kimi-latest` | 当前文档已无 | | | | 关 |

K2 计划 2026 年 12 月底下线。多轮与工具调用须带回完整的助手消息。
来源：[价格](https://platform.kimi.com/docs/pricing/chat)、[对话接口](https://platform.kimi.com/docs/api/chat)、[K3 快速开始](https://platform.kimi.com/docs/guide/kimi-k3-quickstart.md)、[视觉模型](https://platform.kimi.ai/docs/guide/use-kimi-vision-model)。

### 3.3 智谱 GLM（`https://open.bigmodel.cn/api/paas/v4`）

| 模型 | 图片 | 工具 | 预填 |
| --- | --- | --- | --- |
| 文本 `glm-4.5*` 至 `glm-5.*` | 否 | 是（流式工具要 `tool_stream: true`） | 工具、思考、缓存 |
| 视觉 `glm-5.3-flash`、`glm-4.6v`、`autoglm-phone` | 是 | 是 | 全部 |
| 视觉 `glm-5v-turbo`、`glm-4.6v-flash`、`glm-4.1v-thinking-*` | 是 | **否** | 图片（4.1v-thinking 加思考）、缓存 |
| `glm-4v-flash` | 只收 1 张、**不收 base64** | 否 | 关（我们会发 base64） |

`tool_choice` 只能是 `auto`。`glm-5.3-flashx` 文档前后不一（文本或视觉），按文本处理。缓存为隐式自动。
来源：[模型概览](https://docs.bigmodel.cn/cn/guide/start/model-overview)、[对话补全](https://docs.bigmodel.cn/api-reference/模型-api/对话补全)、[缓存](https://docs.bigmodel.cn/cn/guide/capabilities/cache)。

### 3.4 OpenAI（`https://api.openai.com/v1`）

| 模型 | 图片 | Chat Completions 上的工具 | 预填 |
| --- | --- | --- | --- |
| `gpt-6*`（astra、sol、terra、luna） | 是 | **否**：文档说函数调用要用 Responses 协议（luna 仅在 `reasoning_effort: none` 时可用） | 图片、缓存 |
| `gpt-4o*`、`gpt-4.1*`（2027-01-14 停用）、`gpt-5*`（2026-12-11 停用） | 是 | 是 | 工具、图片、缓存 |

Chat Completions 不返回思考内容；缓存自动。
来源：[模型](https://developers.openai.com/api/docs/models)、[gpt-6.1-terra](https://developers.openai.com/api/docs/models/gpt-6.1-terra)、[gpt-6.1-sol](https://developers.openai.com/api/docs/models/gpt-6.1-sol)、[gpt-6-luna](https://developers.openai.com/api/docs/models/gpt-6-luna)、[gpt-4.1](https://developers.openai.com/api/docs/models/gpt-4.1)、[推理](https://developers.openai.com/api/docs/guides/reasoning)、[提示缓存](https://developers.openai.com/api/docs/guides/prompt-caching)、[停用表](https://developers.openai.com/api/docs/deprecations)。

### 3.5 Claude 经 OpenAI 兼容接口

- **Anthropic 自己的兼容层**（`https://api.anthropic.com/v1/`，官方说明主要用于测试比较、不建议生产）：图片与工具都支持；`strict` 被忽略；Opus 5.5、Sonnet 5.5 等不接受 required／指定函数的 `tool_choice`；**思考内容与提示缓存丢失**；Claude 4.7 起 `temperature` < 1 或改 `top_p` 会 400。预填：`claude-*` → 工具、图片。来源：[OpenAI SDK 兼容](https://platform.claude.com/docs/en/api/openai-sdk)。
- **经 OpenRouter**：图片、工具保留；思考内容在 `reasoning` 参数开启时返回，带工具时 `reasoning_details` 须原样带回；缓存保留但**不自动**，要 `cache_control`。来源：[提示缓存](https://openrouter.ai/docs/guides/best-practices/prompt-caching)、[思考 token](https://openrouter.ai/docs/guides/best-practices/reasoning-tokens)。
- **其他中转**（自定义地址）：能否透传图片和工具取决于中转本身，预设一律关闭，由用户打开。

### 3.6 阿里云百炼（`https://dashscope.aliyuncs.com/compatible-mode/v1`）

| 模型 | 图片 | 工具 | 预填 |
| --- | --- | --- | --- |
| `qwen4-max*`、`qwen3.7-plus*` | 是 | 是 | 全部 |
| `qwen3-vl-*` | 是 | 是 | 全部 |
| `qwen-vl-*` 等旧视觉模型 | 是 | 未列出 | 图片、缓存 |
| `qwen4-*`、`qwen3.5`–`3.8` 的 max／plus／flash | 否 | 是 | 工具、思考、缓存 |
| `qwen-max／plus／flash／turbo*` | 否 | 是 | 工具、缓存 |
| 托管的 `kimi-k3`、`glm-5.3`、`deepseek-v4-pro` | 按原厂 | 按原厂 | 按原厂规则 |

思考模式不支持 `tool_choice: required`；旧兼容页写“流式不能用工具”，新函数调用页相反（待核实）。新的工作空间专属地址形如 `https://{WorkspaceId}.cn-beijing.maas.aliyuncs.com/compatible-mode/v1`，不匹配预设，按未知处理。
来源：[函数调用](https://help.aliyun.com/zh/model-studio/user-guide/qwen-function-calling)、[qwen4-max](https://help.aliyun.com/zh/model-studio/qwen4-max)、[深度思考](https://help.aliyun.com/zh/model-studio/deep-thinking)、[视觉](https://help.aliyun.com/zh/model-studio/vision)、[上下文缓存](https://help.aliyun.com/zh/model-studio/user-guide/context-cache)、[模型列表](https://help.aliyun.com/zh/model-studio/models)、[OpenAI 兼容](https://help.aliyun.com/zh/model-studio/compatibility-of-openai-with-dashscope)、[地址](https://help.aliyun.com/zh/model-studio/base-url)。

### 3.7 OpenRouter（`https://openrouter.ai/api/v1`）

`GET /models` 的每个模型带 `architecture.input_modalities`（含 `image` 即可看图）、`supported_parameters`（含 `tools`、`reasoning`）、`context_length`。这是唯一能可靠自动判断的来源，后续应改为读它。当前预填：取 `厂商/模型` 斜杠后的部分按原厂规则判断，缓存一律关（并非对所有厂商自动）。注意：模型的所有服务方都不支持工具时，`tools` 会被**静默忽略**；发工具时应带 `provider.require_parameters: true`。
来源：[模型](https://openrouter.ai/docs/guides/overview/models)、[模型列表接口](https://openrouter.ai/docs/api-reference/list-available-models)、[服务方选择](https://openrouter.ai/docs/guides/routing/provider-selection)、[工具调用](https://openrouter.ai/docs/guides/features/tool-calling)。

### 3.8 硅基流动（`https://api.siliconflow.cn/v1`）

支持 `image_url` 与 `tools`，但哪些模型支持只在“模型广场”的 VLM／Tools 标签里，`/models` 不带能力信息。预填与 OpenRouter 相同（按原厂规则，缓存关），待核实。
来源：[对话接口](https://docs.siliconflow.cn/cn/api-reference/chat-completions/chat-completions)、[函数调用](https://docs.siliconflow.cn/en/userguide/guides/function-calling)、[模型列表](https://docs.siliconflow.com/en/api-reference/models/get-model-list.md)。

### 3.9 Ollama（`http://localhost:11434/v1`）

兼容层支持图片（**只收 base64**）与 `tools`（不支持 `tool_choice`），但取决于本机装的模型。原生 `POST /api/show` 返回 `capabilities`（`vision`、`tools`、`thinking`）。当前预填全关；以后可读 `/api/show`。本机免费，可自行核实。
来源：[OpenAI 兼容](https://docs.ollama.com/api/openai-compatibility.md)、[上下文长度](https://docs.ollama.com/context-length)、[API](https://github.com/ollama/ollama/blob/main/docs/api.md)。

## 4. 待真实调用核实（付费，由你决定）

1. DeepSeek `deepseek-flash`：图片＋工具同一请求；`deepseek-chat` 是否仍可用。
2. Kimi K3：base64 图片。
3. OpenAI GPT-6 terra／luna：Chat Completions 上的工具行为。
4. 百炼：`stream=true` 时的工具调用。
5. 硅基流动：按厂商前缀推断是否准确。

每项一两次很短的请求即可；都不做时，预填仍可用，只是可能有少数模型标错，用户可在设置里改。

## 5. 给阶段 1、2 与 agent-docs 的约定

- **界面位置。** 声明能力的地方是 **设置 › 模型连接 › 高级设置 › 模型能力**，勾选项文字为“工具调用”“图片输入”。需求草稿若写“设置 › 模型”，请改成这个路径（或告诉我改界面）。
- **术语表。** `ModelCapabilities` 与新增的 `ModelCapability` 尚未进[架构 §2.3](../architecture.md#23-代码术语表)；建议加一行“模型能力 → `ModelCapabilities`（单项 `ModelCapability`）”，随需求改动一并由你确认。
- **怎么用。** 显示附件或工具入口前问 `ActiveModelHasAsync`；发送时问快照 `Capabilities.Has`；设置保存会触发 `ModelSettingsService.Changed`，入口可据此刷新。
- **阶段 1（图片）** 一律发 base64 data URL：Kimi 与 Ollama 不收公网 URL。阶段 5 的 `AudioInput` 需要在 `ModelCapabilities` 加字段（JSON 列，无需迁移，旧行读作 false）。
- **阶段 2（工具）** 只发 `tool_choice: "auto"`（GLM、Ollama、思考模式的 DeepSeek／Qwen、Claude 5.5 都拒绝其他值）；DeepSeek、Kimi、OpenRouter 上的 Claude 在带工具的多轮里要原样带回思考内容；OpenRouter 加 `provider.require_parameters: true`。
