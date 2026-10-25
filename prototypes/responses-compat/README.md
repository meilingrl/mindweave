# Responses HTTP 兼容验证

状态：使用中的独立、可丢弃验证工具。用户已授权小规模兼容验证；脚本已做本地解析／拒绝重定向检查；用户已在本地输入密钥完成真实小规模调用，结果见下方记录。不是正式产品实现，不修改两套 GUI 原型或其数据。

## 本地运行

```sh
python3 /Users/meilingluo/Developer/Projects/mindweave/prototypes/responses-compat/probe.py
```

在本地终端提示处粘贴密钥并回车，输入不会显示。不要把密钥放进命令、聊天、脚本或结果文件。脚本仅在进程内使用凭据，不读取已有 Codex 凭据。固定目标 `https://api.pinaic.com/v1/responses`，模型 `gpt-6.1-sol`，`store=false`；不允许重定向、不使用环境代理。`store=false` 不证明第三方实际留存政策。

最多一次无效密钥请求及三次真实模型请求（普通、HTTP SSE、收到首段文字后主动关闭），每次输出上限 1024 tokens；可能计费，不自动重试，不调用模型发现、WebSocket 或原生压缩。30 秒套接字超时，事件间检查总时长，Ctrl+C 可中断；这不是严格独立看门狗截止时间。基础请求未通过则不再发后两次真实请求。

结果自动写入本目录 `results/` 下的时间命名 JSON，保存状态、计数、耗时和脚本哈希，不保存请求头、密钥及正文。可将结果路径告知助手读取。HTTP 401/403 作为无效密钥检查证据；普通回答须明确 completed 且有文本；SSE 须有文本增量和 completed。连接结束不等于成功。主动停止只证明客户端关闭且已收到文字，不证明服务器停止计费，也不证明正式产品持久保存了部分回答。

若失败，本轮不自动增加参数重试或降低模型；分析脱敏结果后再确定后续。JSON 解析失败仅报告错误类型，不转储服务器原文。

## 本地检查

```sh
python3 /Users/meilingluo/Developer/Projects/mindweave/prototypes/responses-compat/probe.py --self-test
```

仅覆盖多行 SSE、CRLF、结束标记、EOF 与拒绝重定向的局部行为。通过不等于供应商兼容或应用验收。真实调用结果与这些局部检查分别记录。

## 实际验证 · 2026-10-23

用户本地运行，助手读取脱敏报告核对，未再次调用服务。

- [gpt-5.5 报告](results/20261023T081504143180Z.json)：无效密钥返回 401，普通请求返回 404，后续真实请求未执行。状态码不足以确定具体失败原因。
- [gpt-6.1-sol 报告](results/20261023T081809928863Z.json)：无效密钥 401；普通请求 200、completed、7 字符；SSE 200、76 次非空文字增量、91 字符、明确 response.completed；主动停止收到 1 次增量后客户端关闭。

本轮验证支持 Pinaic 的 gpt-6.1-sol Responses HTTP/SSE 作为首个实际接入目标；不证明所有模型、Chat Completions、WebSocket、原生压缩、多轮上下文、长文、重试及正式 C# 集成兼容。停止项不证明服务端取消或停止计费。测试没有保存正文，无法据此检查回答内容质量。首版协议变更应同步到正式设计后再实施。

## 多模型／多协议探针 · 2026-10-23

新增独立 `multi_probe.py`，保留原 Responses 脚本和实测报告。状态：6 项本地单元检查通过，真实调用待用户运行；测试先观察到模块尚不存在的失败，再实现解析及候选选择。检查涵盖目录候选、Chat 完成／截断、Messages 完成／无终态、普通回复需要有效文本。

```sh
python3 /Users/meilingluo/Developer/Projects/mindweave/prototypes/responses-compat/multi_probe.py
```

分别隐藏输入 GPT、Claude、DeepSeek 专用密钥（可回车跳过），各自读取 `/v1/models`，每类自动选一个对应目录实际返回的候选，不要求用户填模型 ID。优先已验证 GPT、常规 Claude 和 DeepSeek 标识，否则按名称稳定选择该类一个候选；不是最佳／最便宜模型排名。目录不包含该类则跳过；发现失败则停止，手动模型输入可在正式产品中保留，但本探针不猜 ID。

独立验证四组：GPT Chat Completions、Claude Messages、同一个 Claude Chat Completions、DeepSeek Chat Completions。每组普通成功才测 SSE，最多 8 次生成、各上限 1024 输出 tokens，不自动重试或换模型。Claude 的两种协议是明确的独立探测，不是正式产品静默回退。Messages 使用 x-api-key 与 anthropic-version；Chat 使用 Bearer。GPT／Claude 固定访问 Pinaic `https://api.pinaic.com/v1`，DeepSeek 固定访问官方 `https://api.deepseek.com`；每组输入前显示地址。若地址不符，应先跳过，不把密钥提交到错误服务。每组发现和生成只使用本组密钥，Claude 的两种协议共享 Claude 密钥。最多 3 次目录读取。第三方若要求其他头或路径，本次失败不足以证明不支持。

保留隐藏输入、不读既有凭据、不持久化密钥、不保存回复和错误正文、不使用环境代理及拒绝重定向。报告 `results/multi-*.json` 增加经过字符检查的模型 ID 和选中项，记录协议／状态／耗时／脚本哈希，不记录其他目录元数据。输出限额可能让推理模型尚未输出文字即截断，记录为未验证而不是可用；响应模型名不是上游身份认证。

```sh
python3 -m unittest discover -s /Users/meilingluo/Developer/Projects/mindweave/prototypes/responses-compat -p test_multi_probe.py
```

协议参考：[Claude SSE](https://platform.claude.com/docs/en/build-with-claude/streaming)、[DeepSeek Chat Completions](https://api-docs.deepseek.com/api/create-chat-completion/)。使用协议格式不证明 Pinaic 已支持；真实结果后续单独记录。

## 多协议实测结果 · 2026-10-23

用户本地执行后，助手读取[脱敏报告](results/multi-20261023T083337487381Z.json)，核对报告中的两个脚本哈希与当前文件一致，未再次调用。三组分别用各自密钥发现模型，DeepSeek 请求目标为官方地址。

| 服务／模型 | 协议 | 普通 | 流式 |
| --- | --- | --- | --- |
| Pinaic / gpt-6.1-sol | Chat Completions | 200，stop，有正文 | 200，30 次文字增量，stop 与流结束标记 |
| Pinaic / claude-sonnet-4-6 | Messages | 502，受阻 | 普通未通过，未执行 |
| Pinaic / claude-sonnet-4-6 | Chat Completions | 403，受阻 | 普通未通过，未执行 |
| DeepSeek 官方 / deepseek-flash | Chat Completions | 200，stop，有正文 | 200，23 次文字增量，stop 与流结束标记 |

Claude 原因由用户补充：上游账号额度耗尽。报告本身仅保留状态码，不能独立确认额度原因；据此记录本次受阻、兼容性未定，既不判不兼容，也不判通过。额度恢复后如需要可单独复测，不重跑已通过组合。

结合前轮 Responses 证据，原定首版 Chat Completions 方案已有 Pinaic GPT 与 DeepSeek 官方两个实际样本支撑，不必因最初 Codex 配置使用 Responses 而强制改换首版协议。这是选型建议，未扩展正式首版多连接 UI、未实现适配器。自动模型发现本轮成功，但目录不证明全部模型可调用或真实上游身份；短样本不替代长文、多轮、压缩、正式 C# 集成与更多故障验证。
