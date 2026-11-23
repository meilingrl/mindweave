using System.Globalization;
using Mindweave.Core.Runs;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Desktop.Text;

// Web search and 出处 (requirements §10.4.5, agent platform phase 3): the composer's switch, the approval card, the 出处 list
// under an answer, the viewer's "open in browser", and 设置 › 工具 › 网络搜索.
public static partial class UiText
{
    public static string WebSearchName => T("Web search", "网络搜索");
    public static string FetchPageName => T("Read web page", "取网页");

    public static string WebSearchAuto => T("Auto", "自动");
    public static string WebSearchOn => T("On", "开");
    public static string WebSearchOff => T("Off", "关");

    public static string WebSearchModeName(WebSearchMode mode) => mode switch
    {
        WebSearchMode.On => WebSearchOn,
        WebSearchMode.Off => WebSearchOff,
        _ => WebSearchAuto,
    };

    /// <summary>The switch's tooltip: what the position does.</summary>
    public static string WebSearchModeTip(WebSearchMode mode) => mode switch
    {
        WebSearchMode.On => T("Web search: On (search at least once for this question)", "网络搜索：开（这次提问至少搜索一次）"),
        WebSearchMode.Off => T("Web search: Off", "网络搜索：关"),
        _ => T("Web search: Auto (the model decides whether to search)", "网络搜索：自动（由模型决定是否搜索）"),
    };

    public static string WebSearchAutoTip => T("The model decides whether to search", "由模型决定是否搜索");
    public static string WebSearchOnTip => T("Search at least once for this question", "这次提问至少搜索一次");

    public static string WebSearchNoKey => T("No search service is set up yet; you can set one up in Settings › Tools › Web search", "还没有设置搜索服务，可在设置 › 工具 › 网络搜索中设置");
    public static string WebSearchToolsOff => T("Tools are turned off in Settings › Tools", "已在设置 › 工具中关闭工具");

    public static string ApprovalTitle => T("The model wants to read a URL that hasn’t appeared in the search results or your messages", "模型想读取一个没有在搜索结果或你的消息中出现过的网址");
    public static string ApprovalAllow => T("Allow", "允许");
    public static string ApprovalDeny => T("Deny", "拒绝");
    public static string ApprovalWaiting => T("Waiting for you to approve reading the URL…", "等待你批准读取网址…");
    public static string RunStepDenied => T("You denied this", "你拒绝了");
    public static string ApprovalExpired => T("Approval expired", "批准已失效");

    public static string CitationsHeading => T("Citations", "出处");

    /// <summary>The line that opens the 出处 list: how many sources the answer cites.</summary>
    public static string CitationsCount(int count) => T($"Citations · {count}", $"出处 · {count}");
    public static string CitationsExpand => T("Show citations", "展开出处");
    public static string CitationsCollapse => T("Hide citations", "收起出处");

    /// <summary>A 出处 row's meta line: the host and when it was fetched.</summary>
    public static string CitationMeta(Uri url, DateTimeOffset fetchedAt) =>
        T(
            url.Host + " · fetched " + fetchedAt.ToLocalTime().ToString("MMM d, yyyy HH:mm", EnglishCulture),
            url.Host + " · 获取于 " + fetchedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));

    /// <summary>The viewer's info line for a 出处.</summary>
    public static string CitationInfo(int number, DateTimeOffset fetchedAt) =>
        T(
            string.Format(CultureInfo.InvariantCulture, "Citation [{0}] · fetched {1}", number, fetchedAt.ToLocalTime().ToString("MMM d, yyyy HH:mm", EnglishCulture)),
            string.Format(CultureInfo.InvariantCulture, "出处 [{0}] · 获取于 {1}", number, fetchedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));

    /// <summary>The viewer's info line for a 出处 in this app's own material (L11).</summary>
    public static string LocalCitationInfo(int number) =>
        T(string.Format(CultureInfo.InvariantCulture, "Citation [{0}]", number), string.Format(CultureInfo.InvariantCulture, "出处 [{0}]", number));

    /// <summary>A local 出处 row's second line (L11): where the material is.</summary>
    public static string CitationPlace(Core.Conversations.CitationKind kind) => kind switch
    {
        Core.Conversations.CitationKind.Attachment => T("An attachment in this conversation", "本会话的附件"),
        Core.Conversations.CitationKind.Material => T("Material fetched earlier in this conversation", "本会话较早取得的材料"),
        Core.Conversations.CitationKind.Summary => T("Summary network", "总结网络"),
        _ => string.Empty,
    };

    public static string OpenInBrowser => T("Open in browser", "在浏览器中打开");
    public static string OpenInBrowserConfirm => T("This address will open in your system browser:", "将在系统浏览器中打开这个地址：");
    public static string OpenInBrowserGo => T("Open", "打开");
    public static string OpenInBrowserCancel => T("Cancel", "取消");
    public static string WebSourceUntrusted => T("From the web; the content is not trusted", "来自网络，内容不可信");

    public static string WebSearchSettingsTitle => T("Web search", "网络搜索");
    public static string WebSearchSettingsIntro => T("With a search service set up, the model can search the web, read web pages and mark citations in its answers. The key is kept in the system credential store.", "设置搜索服务后，模型可以搜索网络、读取网页，并在回答中标出出处。密钥保存在系统凭据中。");
    public static string WebSearchService => T("Search service", "搜索服务");
    public static string WebSearchKey => T("Key", "密钥");
    public static string WebSearchKeySet => T("Set", "已设置");
    public static string WebSearchKeyNotSet => T("Not set", "未设置");
    public static string WebSearchKeyPlaceholder => T("Paste a new key", "粘贴新的密钥");
    public static string WebSearchKeySave => T("Save key", "保存密钥");
    public static string WebSearchKeyRemove => T("Remove key", "移除密钥");
    public static string WebSearchTest => T("Test", "测试");
    public static string WebSearchTestTip => T("Runs one real search (counts toward your search service usage)", "发一次真实的搜索（计入搜索服务的用量）");
    public static string WebSearchTesting => T("Testing…", "正在测试…");
    public static string WebSearchTestOk => T("Working", "可用");
    public static string WebSearchDefault => T("Default for the switch beside the input box","输入框开关的默认档");
    public static string WebSearchKeySaved => T("Key saved", "密钥已保存");
    public static string WebSearchKeyRemoved => T("Key removed", "密钥已移除");
    public static string WebSearchSaveFailed => T("Couldn’t save. Check that the system credential store is available.", "未能保存，请检查系统凭据是否可用。");

    /// <summary>The name of a 出处 as a person reads it: a page's title, or Attachment “…” / 附件《…》 and so on.</summary>
    public static string CitationLabel(Core.Conversations.CitationKind kind, string title) => kind switch
    {
        Core.Conversations.CitationKind.Attachment => T($"Attachment “{title}”", $"附件《{title}》"),
        Core.Conversations.CitationKind.Material => T($"Material “{title}”", $"材料《{title}》"),
        Core.Conversations.CitationKind.Summary => T($"Summary “{title}”", $"总结《{title}》"),
        _ => title,
    };
}
