# S3 真实摘要质量 · 测试材料与执行约束

状态：**已真实发起5次调用，完整摘要过长触发最终预算拒绝，暂停并修订压缩提示**。用户允许放宽原8次／1024上限，控制器设本轮最多12次生成、普通回答最多4096，用户明确选择flash后才外呼。首次摘要1024被截断；Task5修复输出上限及格式2兼容后，摘要4096完整但冗长，最终回答仍未发送。Task6仅修订压缩篇幅和去重提示，并保留格式2／3精确兼容。思考默认、测试三轮材料及12次总上限不变，S1剩余一次生成未用。实现与结果见 [S3 验证](s3-verification.md)。

原独立入口在 `artifacts/macos-s3-quality/Mindweave Codex.app`，Task5副本在macos-s3-quality-budget，数据根目录同为 `artifacts/s3-supplier-native`；仅Launch数据路径区别于对应合成包，旧包和原数据均保留。schema5库只包含本组三轮虚构输入，配置官方地址／flash／10000预算／4096普通输出／max_tokens；密钥用户本机录入，未读取凭据值或继承S1凭据。当前2 Completed Reply／1 Completed Title／2 Failed Reply、1 Completed源摘要＋1 Failed源尝试；有效缓存1、prepared2、0活动、FK空。原问题、确认和旧尝试保留，应用正常关闭。

## 有界执行方案

只使用以下虚构材料，不发送个人内容、真实业务资料或S1/S2会话。本轮最多12次生成，含所有压缩、回答和标题；普通回答／新摘要最多4096，标题最多128，不查询目录。固定输出补救Task5已过审；Task6提示修订过审及后验前不重试。当前实际5次，仍余7次；接受记录保守计数含未发出的父Reply，不能冒充实际外呼或账单。此为调用与输出上限，不是金额报价，不因允许放宽就用满。

三轮问题分别为下方 U1、U2、U3。目标是让 U1 成为较早摘要材料，U2 保持近期原文，U3 提问时实际触发压缩。原预填预算7000／输出1024，外呼前放宽为10000／4096；它们是本应用的保守估算配置，不表示供应商模型窗口大小。

控制器已用当前纯规划器和假设的两条 `Recorded.` 短回答做离线形状检查：U1／U2／U3 分别为2844／1853／1542 ASCII字符；7000／1024与10000／4096两组预算下，三轮摘要数均为0／0／1，第三轮全部U1进入摘要切片、U2保留原文，每份摘要请求均在预算内。正常路径预计3次回答＋1次标题＋1次摘要，共5次，不自动重试。此预演没有 transport、凭据或外呼，使用占位配置，不能替代真实已保存回答与实际配置的逐轮预检，更不是已通过质量验收。

确认模型并保存flash后，三次实际配置离线预检均执行且草稿逐字匹配：U1估计7228／0摘要，U2估计9154／0摘要，U3估计10769／1摘要。第三轮计划覆盖全部U1及首条确认，U2与其确认保留原文。曾在模型确认前对pro仅做过离线预检，没有向pro发请求。预检未读取凭据值。

每次点击发送前，控制器用同版本纯规划器，针对数据库内已保存的准确消息、下一条问题和当前配置做**离线预检**，不读取凭据值或外呼。将全部已接受父任务／源尝试、计划新增摘要和回答，以及一次标题预留计入上限；不扣除缓存命中，宁可保守多计。总数超过12则不发送。已保存来源身份和 cache key 使用实际配置；尚未接受的新问题使用临时ID，只作为受保护原文，不作为缓存来源，不将其冒充真实接受身份。

若实际回答偏离短确认、三轮未触发压缩、必要原文预检拒绝，先停止并记录；不得靠重复发送、附加填充、改预算重试或自动续轮扩大已审查材料。可在外呼前调整预算并重新离线预检，但配置须固定后再开始测试，并记录最终值。任何流失败、结构无效或截断先记录真实失败，不擅自提高输出上限、关闭思考或换模型。

本地设置与数据隔离沿用现有机制，真实密钥仅由用户在本机录入；录入期间停止 AX／截图读取。授权和本机配置就绪前，不启动真实请求。测试结束正常关闭，保存全部旧尝试，不清理原数据。

## U1：早期决定、纠正、来源与指代

整段作为第一条问题，要求仅短确认，不能先问最终答案。

```text
This is a fictional planning notebook for a student audio exhibit called Lantern. Store the facts for later questions. Reply only "Recorded." Do not summarize or repeat the facts in your answer.

Decision record D1 was an initial draft. It proposed Saturday at 10:00, Room 301, and eight public visitors. That draft was never the final invitation. Keep its text as historical context, but do not present its proposal as the accepted arrangement.

Decision record D2 corrected D1 after a room check. The accepted arrangement is Sunday at 14:00 in Room 302. There are six public visitors and two student staff members. The two staff members are not part of the public visitor count. D2 is the source to cite for the accepted time, room, and attendance. D1 remains in the notebook only to explain what changed. No later message has reversed D2.

The borrowed equipment consists of a portable recorder and a spare battery. A separate note discusses the spare battery: it must not be left charging overnight. In this sentence, "it" refers to the spare battery, not the recorder. The recorder has no overnight-charging instruction in this notebook. Do not merge two different equipment items into one object when reporting this rule.

An open question is how the equipment will travel to the venue. A bicycle courier and a staff pickup were both suggested. Neither option was approved, and nobody was assigned responsibility. A later answer must keep this undecided instead of selecting whichever option seems convenient.

Archive note A concerns the exhibit's listening cards. Each card has a plain heading, a short transcript, and a location number. Volunteers can check whether the heading matches the clip without playing every clip again. Cards with unclear labels are placed in a review folder. This review folder is an editing queue and does not imply that a clip has been rejected. A clip stays available until an explicit removal decision is written down.

Archive note B concerns the table layout. The left side holds demonstration objects, the center holds listening cards, and the right side holds blank feedback sheets. A pencil tray may move between tables as people need it. Table arrangement is an operational convenience, not a source of changes to the invitation. An empty feedback sheet means that no response was written on that sheet; it does not establish that a visitor disliked the exhibit.

Archive note C concerns the end-of-session checklist. Volunteers count reusable cards, collect pencils, and list damaged sleeves. They write observations separately from recommendations. A suggestion to buy more sleeves is not a purchase approval. An incomplete count is marked incomplete rather than filled in from memory. These archive notes add background for the notebook; they do not replace D2 or resolve the transport question.
```

## U2：近期独立材料

这一轮不得主动重复 U1 的关键事实，避免最终回答只从受保护的近期原文直接取到答案。

```text
Add this independent operations note to the same fictional notebook. Reply only "Recorded." Do not repeat earlier decisions or answer questions about them yet.

Feedback preparation uses three empty trays named Start, Review, and Filed. Start holds blank sheets. Review holds sheets awaiting transcription. Filed holds sheets that have already been transcribed and checked. A sheet moves forward only after a volunteer checks the transcription. Moving a tray on the table does not change the state of the sheets inside it. Tray names are workflow labels, not ratings of the visitor or the exhibit.

For transcription, copy each legible comment into its own row. Leave an unreadable word marked unreadable. Do not guess a name, add a positive tone, or turn a question into a statement. Keep the original sheet number beside the copied text. If two volunteers disagree, record both readings in a review note and leave the row unresolved until they inspect the sheet together. A corrected row retains a short explanation of the correction.

The display board has a space for frequently asked questions. A question can be added there only after checking that its answer exists in the notebook. A missing answer remains a question. Draft wording is kept in a separate working list so that visitors do not mistake a draft for a decision. Visual neatness is useful, but it does not authorize a volunteer to invent a missing fact.

The cleanup folder contains a count sheet, an observations sheet, and a suggestions sheet. Keep those purposes separate. A count records objects actually checked. An observation records something someone saw. A suggestion proposes a future action. None of these headings automatically approves a proposal or revises an earlier decision. This note adds no new invitation, equipment rule, source decision, or transport arrangement.
```

## U3：最终核对问题

附带的交接说明增加实际上下文负担，但不重复期望答案。先离线确认 U1 的关键片段确实进入摘要计划，最终请求中没有相同答案从近期原文泄漏；否则该样本不能证明摘要承载了这些事实。

```text
Use the notebook to answer the five questions below in Chinese, briefly. Use accepted decisions rather than superseded drafts. If the notebook does not decide something, say so. Do not infer an approval from a suggestion.

1. What are the accepted day, time, and room for Lantern?
2. How many public visitors and how many student staff members are expected? Keep the two categories separate.
3. Which decision record supports the accepted arrangement, and how does it relate to the earlier draft?
4. In the earlier overnight-charging sentence, what does "it" refer to? State the rule for that object without extending it to another object.
5. Has transport been decided? Give only the status supported by the notebook.

For the handover, distinguish facts, open questions, and suggested actions. A useful handover should let another student find the source rather than merely trust a confident sentence. Avoid treating repeated wording as additional evidence. Do not turn the absence of a new correction into proof that every possible detail is known. An accepted arrangement can coexist with an unresolved operational question.

The handover is a short reading aid. It is not a replacement for the original notebook. Keep uncertainty attached to the particular claim that is uncertain, rather than making the whole answer vague. Do not add travel advice, a shopping list, or an imagined explanation for why a decision was made. The final answer should address only the five questions, with a source reference where the notebook provides one.
```

## 评判与证据

| 项目 | 期望 |
| --- | --- |
| 当前决定 | Sunday／14:00／Room 302；不把 Saturday／10:00／301 当成当前安排 |
| 纠正与来源 | 明确 D2 更正 D1；六名公众访客与两名学生工作人员分开，不写成八名公众访客 |
| 指代 | “it”指备用电池；不得把过夜充电规则扩展到录音机 |
| 未决事项 | 运输未决定；两个建议均不等于批准或分工 |
| 实际承载 | 核对计划、源切片、胜出摘要ID和 prepared 引用；关键早期资料实际通过摘要进入最终请求 |

分别评判**摘要本身**和**最终回答**。摘要漏掉事实而最终回答猜中，不能记作摘要忠实；摘要正确但最终回答误读，也应单独记录。只看到最终文字正确而没有核实实际使用摘要，不能算 S3 质量样本。结构有效与语义正确是两项结果。

执行后在 S3 验证记录补充准确源码、配置、预算、调用明细（回答／标题／压缩分别计数）、终态及错误、原始问题保留、摘要与最终回答逐项结果。保存 request hash 和引用身份即可核对本地一致性，不把 hash 当作供应商已收取请求的证明。不保存 Authorization、密钥或推理原文。

这是一个受控虚构样本，不能证明所有长会话、所有分支或所有模型都不会丢信息。Windows、真实双槽并发、客户端取消后的账单及长期学习价值仍按各自证据处理。
