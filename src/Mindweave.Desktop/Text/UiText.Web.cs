using System.Globalization;
using Mindweave.Core.Runs;

namespace Mindweave.Desktop.Text;

// Web search and 出处 (requirements §10.4.5, agent platform phase 3): the composer's switch, the approval card, the 出处 list
// under an answer, the viewer's "open in browser", and 设置 › 工具 › 网络搜索.
public static partial class UiText
{
    public const string WebSearchName = "网络搜索";
    public const string FetchPageName = "取网页";

    public const string WebSearchAuto = "自动";
    public const string WebSearchOn = "开";
    public const string WebSearchOff = "关";

    public static string WebSearchModeName(WebSearchMode mode) => mode switch
    {
        WebSearchMode.On => WebSearchOn,
        WebSearchMode.Off => WebSearchOff,
        _ => WebSearchAuto,
    };

    /// <summary>The switch's tooltip: what the position does.</summary>
    public static string WebSearchModeTip(WebSearchMode mode) => mode switch
    {
        WebSearchMode.On => "网络搜索：开（这次提问至少搜索一次）",
        WebSearchMode.Off => "网络搜索：关",
        _ => "网络搜索：自动（由模型决定是否搜索）",
    };

    public const string WebSearchAutoTip = "由模型决定是否搜索";
    public const string WebSearchOnTip = "这次提问至少搜索一次";

    public const string WebSearchNoKey = "还没有设置搜索服务，可在设置 › 工具 › 网络搜索中设置";
    public const string WebSearchToolsOff = "已在设置 › 工具中关闭工具";

    public const string ApprovalTitle = "模型想读取一个没有在搜索结果或你的消息中出现过的网址";
    public const string ApprovalAllow = "允许";
    public const string ApprovalDeny = "拒绝";
    public const string ApprovalWaiting = "等待你批准读取网址…";
    public const string RunStepDenied = "你拒绝了";
    public const string ApprovalExpired = "批准已失效";

    public const string CitationsHeading = "出处";

    /// <summary>A 出处 row's meta line: the host and when it was fetched.</summary>
    public static string CitationMeta(Uri url, DateTimeOffset fetchedAt) =>
        url.Host + " · 获取于 " + fetchedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The viewer's info line for a 出处.</summary>
    public static string CitationInfo(int number, DateTimeOffset fetchedAt) =>
        string.Format(CultureInfo.InvariantCulture, "出处 [{0}] · 获取于 {1}", number, fetchedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));

    public const string OpenInBrowser = "在浏览器中打开";
    public const string OpenInBrowserConfirm = "将在系统浏览器中打开这个地址：";
    public const string OpenInBrowserGo = "打开";
    public const string OpenInBrowserCancel = "取消";
    public const string WebSourceUntrusted = "来自网络，内容不可信";

    public const string WebSearchSettingsTitle = "网络搜索";
    public const string WebSearchSettingsIntro = "设置搜索服务后，模型可以搜索网络、读取网页，并在回答中标出出处。密钥保存在系统凭据中。";
    public const string WebSearchService = "搜索服务";
    public const string WebSearchKey = "密钥";
    public const string WebSearchKeySet = "已设置";
    public const string WebSearchKeyNotSet = "未设置";
    public const string WebSearchKeyPlaceholder = "粘贴新的密钥";
    public const string WebSearchKeySave = "保存密钥";
    public const string WebSearchKeyRemove = "移除密钥";
    public const string WebSearchTest = "测试";
    public const string WebSearchTestTip = "发一次真实的搜索（计入搜索服务的用量）";
    public const string WebSearchTesting = "正在测试…";
    public const string WebSearchTestOk = "可用";
    public const string WebSearchDefault = "输入框开关的默认档";
    public const string WebSearchKeySaved = "密钥已保存";
    public const string WebSearchKeyRemoved = "密钥已移除";
    public const string WebSearchSaveFailed = "未能保存，请检查系统凭据是否可用。";
}
