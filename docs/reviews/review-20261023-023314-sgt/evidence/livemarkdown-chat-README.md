# Mindweave · LiveMarkdown 组件小样

**状态：使用中、可丢弃的独立技术验证资产。** 本轮验证原生 Markdown 的跨段选择、来源锚点、重排和追加输出；没有迁移或替换[已验收的 Avalonia 11 聊天原型](../avalonia-chat/README.md)，也未开始正式产品开发。界面为灰底黑字的测试窗口，尚未精修样式。

## 运行

本机 .NET SDK 固定为 **10.0.402**，项目使用 **Avalonia 12.1.4** 与固定版本 **LiveMarkdown.Avalonia 2.5.1**。后者包声明依赖 Avalonia 12.0.0 起，与旧原型的 Avalonia 11 分开验证。

```sh
dotnet run --project src/LiveMarkdownSpike/LiveMarkdownSpike.csproj
```

macOS Apple Silicon 自包含包：`artifacts/Mindweave LiveMarkdown Spike.app`。可直接打开；重新生成：

```sh
bash scripts/package-macos.sh
open 'artifacts/Mindweave LiveMarkdown Spike.app'
```

Bundle ID 为 `local.mindweave.livemarkdown-spike`，版本 0.1.0，本机临时签名、未公证。更新包前先关闭该小样；旧聊天原型有独立包名和 Bundle ID。

## 体验与存档

1. 选择「同段重复词」，鼠标拖选第二个「蒸汽机」，底栏应显示局部范围 `[6,9)`；点击「保存选区」。
2. 选择「跨格式 · 跨两段」，从粗体文字穿过链接／行内代码，拖到下一段；保存后可恢复多个区块的选择。
3. 「修改正文试拒绝」会插入新正文；此时「恢复来源」必须报拒绝，不能跳到相似文字。
4. 选择「长文阅读」，在中间阅读或选择文字，再「追加流式文本」；下方独立回复逐块追加固定内容。可停止，未连接 AI。
5. 缩窄窗口应真实换行；滚离后「恢复来源」会把选区起始行带入视口。关闭并重新启动，可恢复最近一次显式保存的样例和选区。

仅「保存选区」写文件；没有自动保存、退出保存或完整会话管理。默认路径：

```text
~/Library/Application Support/MindweaveLiveMarkdownSpike/anchor.json
```

可在启动前用环境变量 `MINDWEAVE_LIVEMARKDOWN_DATA_FILE` 指定独立绝对文件路径：

```sh
MINDWEAVE_LIVEMARKDOWN_DATA_FILE="$PWD/artifacts/manual-anchor.json" dotnet run --project src/LiveMarkdownSpike/LiveMarkdownSpike.csproj
```

存档包含样例正文、当时的固定回复文字、所选消息和序列化锚点，写入同目录临时文件后替换。读取失败时保留原文件，本次禁止覆盖。需要重置时先关闭小样，手动将其 `anchor.json` 改名保留，再启动。它不读写旧原型的 `MindweavePrototype/state.json`，也不提供多实例协调或生产级存储保证。

## 锚点实际含义

适配只用公共 API：遍历可见 `MarkdownTextBlock`，记录其 `SourceSpan`、相同来源块的序号、`LayoutText` 局部选区、引用文字及 SHA-256 校验。没有反射、vendor patch、WebView，也没有全局搜索相同词来猜来源。

- `SourceStart/SourceEnd` 是控件对应的**整个源文区块**，末尾包含在内；不是带格式选区的精确原文范围。
- `Start/Length` 指向 `LayoutText`，界面记为半开区间 `[Start, Start+Length)`。位置均为从 0 开始的 UTF-16 索引。
- 仅当整个源文块与 `LayoutText` 完全相同时才设置 `ExactRawStart`，且恢复时重验该关系。粗体、链接、行内代码等带格式块保存的是**显示锚点**，`ExactRawStart` 为 null；未宣称通用 Markdown 原文逐字符映射。
- 恢复先验证捕获正文前缀的 hash，再验证每段 `SourceSpan`、序号、布局文字 hash 和引用范围；全部通过后才设置原生选区及命名高亮。即使相同词仍在，正文被改写也拒绝。
- 只允许原正文前缀不变、被选块来源与布局也不变的追加。追加闭合 Markdown 标记若改变被选块，就拒绝恢复，不保证正在增长尾块的锚点稳定。

UI 将旧回答与新回复分为两个 `MarkdownRenderer`。另有原生库测试直接在同一长文 renderer 上追加，验证旧块控件、选区和视口保留。`MarkdownUpdateProducer` 在后台对完整快照执行 Markdig Parse，再增量更新视觉控件；这不是增量 Markdown 解析器。窗口内的字符阅读定位仅用于本次调整尺寸／追加，不是完整持久阅读模型。

## 验证记录 · 2026-10-23

```sh
dotnet test tests/LiveMarkdownSpike.Tests/LiveMarkdownSpike.Tests.csproj -c Release
dotnet build src/LiveMarkdownSpike/LiveMarkdownSpike.csproj -c Release
```

最终独立复核：**Debug、Release 均 8/8 通过；Release 构建 0 警告、0 错误**。应用包签名校验及 Info.plist 校验通过。Headless + Skia 用真实文字布局／鼠标命中覆盖重复词、跨格式跨段、hash 拒绝、追加校验、窄窗几何、原文范围一致性、原生 producer 旧选区／视口保留，以及保存后新窗口恢复。文件测试仅用临时路径。

测试先行的红绿记录：Capture 未实现时两项失败，适配后通过；Reveal 未实现时窄窗来源用例失败，实现后通过；篡改第二处的 `ExactRawStart` 为第一处时，最初错误地接受，补上显示／原文一致性校验后拒绝并通过回归。原生 producer 测试将粗体／链接／代码标记拆开追加，验证旧控件引用、选区和视口 Y 保持。

本轮 macOS 应用包的实际操作已验证：

- 鼠标选中第二个「蒸汽机」，范围为 `[6,9)`；保存、关闭到进程确实结束，再启动后仍恢复 `[6,9)`。
- 修改正文后明确拒绝恢复。
- 从粗体「蒸汽机」穿过链接与行内代码拖到第二段「内燃机」，捕获两段 `[2,35)`、`[0,10)`；保存后追加流式回复，旧选择保持。
- 窗口从 1160 拖窄至约 852，正文换行且选择仍准确；滚离后恢复来源可见。
- 再次关闭进程并启动，跨两段的选区准确恢复。当前测试包保留供试用。

这些进程退出／重启是实际桌面证据；自动文件用例只是同一测试进程中的新窗口验证，两者不混称。旧聊天原型中用户已手动确认的拖选／分支／返回流程见其 README；本小样没有实现分支会话。

## 边界

本轮四个目标场景已获得当前 macOS 的技术验证，不等于正式产品或所有 Markdown 的验收。表格、公式、图片、嵌入控件、复杂 Unicode／双向文字、任意文档编辑后的精确原文映射、Windows、Intel Mac 和辅助功能尚未验证。没有真实 AI、图结构、完整聊天、生产级持久化或性能基准；持续全量 Parse 的长文成本需要正式方案另测。

| 入口 | 用途 |
| --- | --- |
| `src/LiveMarkdownSpike/` | 小样窗口、固定样例和来源锚点适配；只列入口，不展开 |
| `tests/LiveMarkdownSpike.Tests/` | 上述原生控件与数据边界回归；只列入口，不展开 |
| `scripts/package-macos.sh` | 生成独立 macOS 自包含应用包 |
| `artifacts/` | 使用中的可重新生成运行产物；只列入口，不展开 |

版本依据：[NuGet 2.5.1](https://www.nuget.org/packages/LiveMarkdown.Avalonia/2.5.1)、[对应源码 v2.5.1](https://github.com/DearVa/LiveMarkdown.Avalonia/tree/v2.5.1)（包记录 commit `fbb6bc1d52980d725d3bedff4f72b762ebd1149b`，Apache-2.0）。使用其公共选择、来源区块、文字几何与高亮 API；本小样仍有自己的显示锚点适配，不是库自动提供完整持久引用。
