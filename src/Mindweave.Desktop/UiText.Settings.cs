using System.Globalization;
using Mindweave.Application.Models;
using Mindweave.Core.Conversations;

namespace Mindweave.Desktop;

// Settings: appearance (D-022) and the model connection.
public static partial class UiText
{
    // Appearance (S7, D-022)
    public const string AppearanceSection = "外观";
    public const string AppearanceFollowSystem = "跟随系统";
    public const string AppearanceLight = "浅色";
    public const string AppearanceDark = "深色";
    public const string ThemeLabel = "主题";

    // Settings pages: presets fill the address; hints only where the page cannot show it otherwise.
    public const string ProviderLabel = "服务商";
    public const string ProviderPlaceholder = "选择服务商";
    public const string ProviderDashScope = "阿里云百炼（通义）";
    public const string ProviderMoonshot = "Moonshot（Kimi）";
    public const string ProviderZhipu = "智谱 GLM";
    public const string ProviderSiliconFlow = "硅基流动";
    public const string ProviderOllama = "Ollama（本机）";
    public const string ProviderCustom = "自定义";
    public const string BaseUrlPlaceholder = "https://…/v1";
    public const string KeySavedPlaceholder = "已保存，留空则继续使用";
    public const string KeyMissingPlaceholder = "尚未保存";
    public const string KeyOtherServicePlaceholder = "请填写此服务的密钥";

    /// <summary>The settings point at a key no longer in the system's credential store (for example after restoring a backup).</summary>
    public const string SavedKeyMissing = "保存的密钥已不在系统的密钥存储中（例如恢复了较早的备份）。请重新填写密钥并保存。";
    public const string OllamaKeyPlaceholder = "本机服务不校验，可填任意内容";
    public const string ConnectionNamePlaceholder = "可选";
    public const string ModelPlaceholder = "选择或输入模型名称";
    public const string ShowModels = "显示模型列表";
    public const string TokensUnit = "token";
    public const string DataSection = "数据";
    public const string LocationSection = "位置";
    public const string OpenFolder = "打开";
    public const string NoBackupsShort = "暂无。数据升级前、连带删除前和恢复备份前会自动备份。";
    public const string AppearanceSaveFailed = "外观设置未能保存，本次运行中仍然有效。";

    // Hints (2026-11-10): whether the open workspace's conversations pop up a hint too (on unless turned off), and the
    // sound for a hint (off unless chosen).
    public const string HintsSection = "提示";
    public const string NewsInWorkspaceLabel = "弹出提示";
    public const string NewsInWorkspace = "当前工作区内也弹出提示";
    public const string NewsInWorkspaceHint = "没在看的会话有新回答时也弹出；关闭后只为其他工作区弹出，侧栏的圆点照常显示。";
    public const string NewsInWorkspaceSaveFailed = "弹出提示设置未能保存，本次运行中仍然有效。";
    public const string NewsSoundLabel = "提示音";
    public const string NewsSound = "新回答提示音";
    public const string NewsSoundHint = "有多个新回答时只响一次";
    public const string NewsSoundSaveFailed = "提示音设置未能保存，本次运行中仍然有效。";

    // Names prepared in the background (2026-11-11): on unless turned off.
    public const string PrepareNamesLabel = "名称建议";
    public const string PrepareNames = "自动准备工作区名称建议";
    public const string PrepareNamesHint = "新会话有了标题后，在后台为工作区想好名称，改名时直接显示；会把标题发给模型，可能产生少量费用。";
    public const string PrepareNamesSaveFailed = "名称建议设置未能保存，本次运行中仍然有效。";

    public const string SettingsTitle = "设置";

    public const string ModelSection = "模型连接";
    public const string ConnectionNameLabel = "名称";
    public const string BaseUrlLabel = "服务地址";
    public const string ApiKeyLabel = "API 密钥";
    public const string ContextWindowLabel = "上下文容量";
    public const string ContextWindowPlaceholder = "留空按 32000 计";
    public const string ContextWindowHint = "对话超出时，较早的内容先压缩成摘要再发送，会多几次请求、多些等待与费用；原始记录完整保留。";

    public const string ModelLabel = "模型";
    public const string FetchModels = "获取列表";
    public const string TestConnection = "测试连接";
    public const string TestCostHint = "可能产生少量费用";
    public const string TestCostTip = "发出一次很短的真实请求。获取模型列表不生成内容。";
    public const string PrivacyHint = "提问与会话内容会发送到此服务；密钥只存于系统钥匙串／凭据管理器。";
    public const string SettingsSaved = "已保存。新的提问将使用此配置；正在进行的回答不受影响。";
    public const string NoModelsFound = "服务没有返回模型，可以直接手动填写模型名称。";
    public const string TestSucceeded = "连接正常。";

    public static string ModelsFound(int count) => string.Format(CultureInfo.CurrentCulture, "找到 {0} 个模型。", count);

    public static string TestFailed(GenerationErrorKind? kind, string? detail) =>
        $"连接失败：{(kind is { } k ? ErrorKind(k) : "未知错误")}{(string.IsNullOrWhiteSpace(detail) ? string.Empty : $"（{detail}）")}";

    public static string SettingsProblem(ModelSettingsProblem problem) => problem switch
    {
        ModelSettingsProblem.InvalidUrl => "服务地址无效，需要以 https:// 开头的完整地址。",
        ModelSettingsProblem.InsecureUrl => "只允许 https 地址（本机地址除外），以免密钥以明文传输。",
        ModelSettingsProblem.MissingModel => "请填写模型名称。",
        ModelSettingsProblem.MissingKey => "请填写 API 密钥。",
        ModelSettingsProblem.CredentialsInUrl => "服务地址里不能包含用户名或密码；密钥请填在“API 密钥”一栏，它会存进系统的密钥存储。",
        ModelSettingsProblem.InvalidContextWindow => $"上下文容量应在 {ModelSettingsInput.MinContextWindow:N0} 到 {ModelSettingsInput.MaxContextWindow:N0} token 之间，不清楚可留空。",
        _ => "设置无效。",
    };

    public static string CredentialUnavailable(string detail) => $"系统凭据服务不可用，请解锁后重试（{detail}）。密钥不会改为明文保存。";
}
