# 固定版本官方资料只读核对

核对日期：2026-10-23（Asia/Singapore）。本轮实际读取官方包元数据和该包指向提交的 README；未运行库、原型或测试。

- [NuGet 2.5.1 元数据](https://api.nuget.org/v3-flatcontainer/livemarkdown.avalonia/2.5.1/livemarkdown.avalonia.nuspec)；[本地原样元数据](livemarkdown-2.5.1.nuspec)。版本 2.5.1，提交 `fbb6bc1d52980d725d3bedff4f72b762ebd1149b`；net8.0/net10.0 依赖组均声明 Avalonia 12.0.0 起、Markdig 1.1.2 起。包许可 Apache-2.0。与项目 csproj 固定 Avalonia.Desktop 12.1.4 / LiveMarkdown 2.5.1 相符，不能据此称正式版已锁版或 Windows 已验证。
- [该提交 README](https://github.com/DearVa/LiveMarkdown.Avalonia/blob/fbb6bc1d52980d725d3bedff4f72b762ebd1149b/README.md)：其内容修改入口按 Append/Clear 组织；这不阻止总结使用独立文本编辑器、保存后重建只读 renderer，不能误报为不能编辑总结。
- 同提交说明：`LayoutText` 是单个文字块排版坐标，嵌入控件以 U+FFFC 占位，内部子文字块有自己的坐标；`ActualText` 为复制用逻辑文本。`SourceSpan` 标识某源版本范围，不是任意编辑后的稳定身份。原型自行保存 hash、span、ordinal 和局部显示选区的做法与该边界相符。
- 库声称可跨 Markdown 元素选择，不等于项目适配已覆盖所有表格、公式、嵌入内容、Unicode 或跨版本引用。本轮不扩展能力声称。

读取方式：web 工具对上述页面失败后，使用 Python 标准库 urllib 只读获取官方 API/raw 资料。未访问 main/latest 文档来反推固定版本。完整官方 README 未复制到本目录。
