using System.Globalization;
using Mindweave.Application.Tools;
using Mindweave.Core.Conversations;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Desktop.Text;

// The failure block (D2 §2.1, requirements §10.8): what to do about a failed request, and its details.
public static partial class UiText
{
    public static string FailureDetails => T("Details", "详情");
    public static string FailureLabelSeparator => T(": ", "：");
    public static string CopyFailureDetails => T("Copy details", "复制详情");
    public static string FailureLoading => T("Reading the request record…", "正在读取请求记录…");
    public static string FailureNoRecord => T("No record of this request was kept; only what the message itself stored is shown.", "没有保留这次请求的记录，只显示消息本身存下的内容。");
    public static string FailureTimeLabel => T("Time", "时间");
    public static string FailureModelLabel => T("Model", "模型");
    public static string FailureStatusLabel => T("Status", "状态");
    public static string FailureRequestIdLabel => T("Request ID", "请求 ID");
    public static string FailureDurationLabel => T("Duration", "用时");
    public static string FailureServiceTextLabel => T("Service said", "服务返回");
    public static string FailureHostLabel => T("Service", "服务");
    public static string OpenModelConnection => T("Open model connection", "打开模型连接");
    public static string McpCommandFailureAdvice => T(
        "Check the server’s command, its arguments and its environment, then start it again.",
        "请检查服务器的命令、参数和环境变量，然后重新启动。");
    public static string McpHttpFailureAdvice => T(
        "Check the server’s address and its headers, then connect again.",
        "请检查服务器的地址和请求头，然后重新连接。");
    public static string WebSearchTestFailed => T("The search test failed.", "搜索测试失败。");

    /// <summary>What to do, one sentence per kind of failure; null when the kind already says it.</summary>
    public static string? FailureAdvice(GenerationErrorKind kind) => kind switch
    {
        GenerationErrorKind.Unauthorized => T("Check the API key and the service address in the model connection.", "请在模型连接中检查 API 密钥和服务地址。"),
        GenerationErrorKind.QuotaOrRateLimited => T("Wait a moment and retry, or check the balance and limits of your account with the service.", "稍等片刻再重试，或到服务方查看账户余额和限额。"),
        GenerationErrorKind.ContextTooLong => T("Ask about a shorter selection or start a branch with less context, or lower the output limit in the model connection.", "请缩小选中的范围或从较短的上下文开分支，也可以在模型连接中调低输出上限。"),
        GenerationErrorKind.ServerError => T("The service had a problem of its own. Retry in a moment.", "服务方自身出了问题，请稍后重试。"),
        GenerationErrorKind.Network => T("Check the network connection and the service address, then retry.", "请检查网络连接和服务地址后重试。"),
        GenerationErrorKind.Timeout => T("The service took too long to answer. Retry, or check the network.", "服务太久没有响应。请重试，或检查网络。"),
        GenerationErrorKind.InvalidResponse => T("Check the service address and the model name in the model connection.", "请在模型连接中检查服务地址和模型名称。"),
        GenerationErrorKind.NotConfigured => T("Set up a model connection first.", "请先设置模型连接。"),
        GenerationErrorKind.StorageUnavailable => T("Check that the disk has free space; saving resumes by itself.", "请确认磁盘还有空间；恢复后会自动继续保存。"),
        GenerationErrorKind.CompressionFailed => T("Retry; if it keeps failing, start a branch with less context.", "请重试；如果一直失败，可以从较短的上下文开分支。"),
        GenerationErrorKind.OutputLimit => T("Raise the output limit in the model connection, or ask the model to continue.", "可以在模型连接中调高输出上限，或让模型接着写。"),
        GenerationErrorKind.ContentFiltered => T("Rephrase the question, or ask another model.", "请换个说法，或换一个模型提问。"),
        _ => null,
    };

    /// <summary>Whether the model connection is where <paramref name="kind"/> is fixed (the block links there).</summary>
    public static bool FixedInModelConnection(GenerationErrorKind kind) =>
        kind is GenerationErrorKind.Unauthorized or GenerationErrorKind.ContextTooLong or GenerationErrorKind.InvalidResponse
            or GenerationErrorKind.NotConfigured or GenerationErrorKind.OutputLimit;

    /// <summary>What to do about a failed web search, one sentence; null when its text already says it.</summary>
    public static string? SearchFailureAdvice(SearchFailureKind? kind) => kind switch
    {
        SearchFailureKind.MissingKey => T("Enter the search service’s key in the web search settings.", "请在网络搜索设置中填写搜索服务的密钥。"),
        SearchFailureKind.KeyRejected => T("Check the search service’s key in the web search settings.", "请在网络搜索设置中检查搜索服务的密钥。"),
        SearchFailureKind.QuotaExhausted => T("Check the credits of your account with the search service.", "请到搜索服务方查看账户额度。"),
        SearchFailureKind.RateLimited => T("Wait a moment and retry.", "请稍等片刻再重试。"),
        SearchFailureKind.Network => T("Check the network connection, then retry.", "请检查网络连接后重试。"),
        SearchFailureKind.Timeout => T("The search service took too long. Retry in a moment.", "搜索服务太久没有响应，请稍后重试。"),
        SearchFailureKind.ServiceError => T("The search service had a problem of its own. Retry in a moment.", "搜索服务自身出了问题，请稍后重试。"),
        _ => null,
    };

    /// <summary>A duration as the details show it: "0.8 s", "12 s", "2 min 5 s".</summary>
    public static string FailureDuration(TimeSpan duration) => duration.TotalSeconds switch
    {
        < 10 => T(string.Create(CultureInfo.InvariantCulture, $"{duration.TotalSeconds:0.0} s"), string.Create(CultureInfo.InvariantCulture, $"{duration.TotalSeconds:0.0} 秒")),
        < 60 => T(string.Create(CultureInfo.InvariantCulture, $"{duration.TotalSeconds:0} s"), string.Create(CultureInfo.InvariantCulture, $"{duration.TotalSeconds:0} 秒")),
        _ => T(
            string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalMinutes} min {duration.Seconds} s"),
            string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalMinutes} 分 {duration.Seconds} 秒")),
    };
}
