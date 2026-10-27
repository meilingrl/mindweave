# S4 独立本地协议夹具

这是专供 S4 原生走查的合成协议服务，输出固定标签和输入中明确给出的测试标记，不是模型，也不验证总结或命名语义质量。旧 `chat_sse_fixture.py`、S1–S3 原生数据、黄金库和测试包均保留。

在稳健轨根目录执行：

```sh
python3 tests/fixtures/summary_sse_fixture.py --port 18084 --delay 3
```

进程只绑定 `127.0.0.1`。服务地址 `http://127.0.0.1:18084/v1`；认证值是公开、非访问密钥 `mindweave-loopback-only`。可用 `--port 0` 自动选择端口，启动行显示实际 endpoint。`--delay` 支持 0–60 秒，只作用于 delayed 模式的总结／命名分块。测试退出后 Ctrl+C 停止服务；它不保存正文、Authorization 或请求材料，不转发远端请求。

控制器从最终已审源启动新的独立数据根目录，例如：

```sh
dotnet run --project src/Mindweave.Desktop -c Release -- --data-root "$PWD/artifacts/s4-native-task8"
```

应用继续在该根目录下建立 `codex-steady/`；不要拿默认用户数据或 S1–S3 原始目录测试迁移。此命令是原生验收入口，不是本任务已执行的原生或打包证据。不可恢复的候选／草稿／正文／节点最终确认须按控制器当次授权边界执行。

在这个新 profile 设置上述 loopback 地址和公开合成 key；模型 `fixture-s4-short`，上下文预算建议 `65536`、输出上限 `8192`，摘要思考模式为“服务默认”。不要把真实服务的凭据引用复制给本地夹具；切换模型前先结束现有任务，受理中的任务仍使用冻结配置。

## 合成材料与场景

1. 会话 A 发送 `这是虚构材料，只供本地协议走查。[MW_DETAIL:观察兰花]`；待回答及自动标题结束，点击“总结当前会话”。第一份完整输出为当前版本。
2. 返回来源会话，发送 `第二轮虚构记录：[MW_DETAIL:记录湿度]`。再次总结生成候选；当前版本保留。采用前可阅读候选或编辑当前，用来核对基线变化的二次确认。
3. 会话 B 发送 `这是另一份虚构材料。[MW_DETAIL:记录温度]` 并总结。两个节点的版本 Title 都为 `S4 合成主题`，触发真实整组命名输入。仅非手动成员输出 `NodeId`/`DisplayName`；后缀仅取该成员当前正文里的 `MW_DETAIL` 标签。没有标签就保留基础标题，不凭编号区分。手动成员是输入成员但不是输出目标。
4. 用 `fixture-s4-long` 生成长正文。包含段落、表格、代码、格式化文字和 Unicode，用于阅读覆盖和位置恢复；候选需先解决才能再次生成。生成或命名采用哪个模型由当次接受冻结的配置决定。
5. 用下表模式验证失败／停止。所有模式的普通回答及自动标题仍正常输出，故可从真实 UI 驱动。S3 压缩请求若出现，只返回原夹具已定义的 `fixture-context` 格式；这与知识总结协议分开。

| 模型 | Summary／Naming 的合成行为 |
| --- | --- |
| `fixture-s4-short` | 完整短 JSON，`stop`＋`[DONE]` |
| `fixture-s4-long` | 完整可滚动 Markdown；命名仍为整组精确成员 JSON |
| `fixture-s4-delayed` | 每 256 字符分块后等待 `--delay`；可观察切页、停止、退出 |
| `fixture-s4-invalid` | 完整 SSE，但总结缺 Markdown／命名目标集合错误 |
| `fixture-s4-truncated` | 半份 JSON，终止原因 `length` |
| `fixture-s4-fail` | 半份 JSON 后断开，没有 `stop`／`[DONE]` |
| `fixture-s4-http-error` | HTTP 503 合成故障 |
| `fixture-s4-after-stop-fail` | 完整 JSON 后发送 `stop`，随后非法 SSE JSON，无 `[DONE]` |

HTTP 尾部延迟不等于传输对象 `DisposeAsync` 延迟；真正迟到 disposal 的确定性证据在既有协调器测试中，不能把夹具观察升级为所有平台的尾任务验收。

## 计数与验证

认证的 `GET /v1/fixture-stats` 只返回按 `summary/naming/reply/title/context` 分类与按模型计数，不包含材料、正文或密钥。快照前后比较可核对保存修复未重发；自动标题／整组命名可能单独增加计数，须按用途解释。统计进程重启后清零；被验证为合法的请求在输出或故障前计数。

```sh
curl -sS -H 'Authorization: Bearer mindweave-loopback-only' http://127.0.0.1:18084/v1/fixture-stats
python3 -m unittest discover -s tests/fixtures -p 'test_*.py'
```

Python 测试各自启动临时端口子进程并在收尾终止，无常驻夹具。S4 复用旧服务的认证、限长 Content-Length/chunked 读取和 SSE writer，不改变旧路由或模式。
