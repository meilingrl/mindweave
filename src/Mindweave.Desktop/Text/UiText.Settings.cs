using System.Globalization;
using Mindweave.Application.Models;
using Mindweave.Application.Workspaces;
using Mindweave.Core.Conversations;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Desktop.Text;

// Settings: appearance (D-022) and the model connection.
public static partial class UiText
{
    // Appearance (S7, D-022)
    public static string AppearanceSection => T("Appearance", "外观");
    public static string AppearanceFollowSystem => T("Follow system", "跟随系统");
    public static string AppearanceLight => T("Light", "浅色");
    public static string AppearanceDark => T("Dark", "深色");

    // The bold colour in answers (D-027).
    public static string FindSettings => T("Find a setting", "查找设置");
    public static string NoSettingFound(string text) => T($"No setting matches “{text}”.", $"没有找到与“{text}”有关的设置。");
    public static string DataAndWorkspaceSection => T("Data and workspaces", "数据与工作区");
    public static string SettingInfo(string? label) => T($"About: {label}", $"说明：{label}");
    public static string PrepareNamesCost => T("Sends the titles of conversations and summaries; may cost a little.", "会把会话与总结的标题发给模型，可能产生少量费用。");
    public static string RecommendationsCost => T("Sends only titles and short excerpts; may cost a little.", "只发送标题和简短摘录，可能产生少量费用。");
    public static string TextWidthLabel => T("Text width", "正文宽度");
    public static string TextWidthWhole => T("Whole page", "整页");
    public static string TextWidthWide => T("Wide", "宽");
    public static string TextWidthMedium => T("Medium", "中");
    public static string TextWidthNarrow => T("Narrow", "窄");
    public static string TextWidthHint => T("For conversations and summaries. Or drag the edge of the text on their pages.", "用于会话和总结；也可以在页面上拖动正文的边缘。");
    public static string TextWidthCustom(int pixels) => T($"Dragged to {pixels} px. For conversations and summaries.", $"已拖到 {pixels} 像素，用于会话和总结。");
    public static string TextWidthGripTip => T("Drag to change the text width, double-click for the default", "拖动调整正文宽度，双击恢复默认");
    public static string BoldColourLabel => T("Bold colour", "加粗颜色");
    public static string BoldColourBrown => T("Brown", "棕色");
    public static string BoldColourSlate => T("Slate", "青灰");
    public static string BoldColourSage => T("Sage", "灰绿");
    public static string BoldColourPlum => T("Plum", "梅紫");
    public static string BoldColourSameAsText => T("Same as text", "与正文相同");
    public static string BoldColourSaveFailed => T(
        "The bold colour could not be saved. It still applies until Mindweave closes.",
        "加粗颜色未能保存，本次运行中仍然有效。");
    public static string ThemeLabel => T("Theme", "主题");

    // Interface language (bilingual-ui.md §7). A language's name is the same in both languages: it is never translated.
    public static string LanguageLabel => T("Language", "语言");
    public static string LanguageEnglish => "English";
    public static string LanguageChinese => "简体中文";

    // Settings pages: presets fill the address; hints only where the page cannot show it otherwise.
    public static string ProviderLabel => T("Provider", "服务商");
    public static string ProviderPlaceholder => T("Choose a provider", "选择服务商");
    public static string ProviderDashScope => T("Alibaba Cloud Model Studio (Qwen)", "阿里云百炼（通义）");
    public static string ProviderMoonshot => T("Moonshot (Kimi)", "Moonshot（Kimi）");
    public static string ProviderZhipu => T("Zhipu GLM", "智谱 GLM");
    public static string ProviderSiliconFlow => T("SiliconFlow", "硅基流动");
    public static string ProviderOllama => T("Ollama (this computer)", "Ollama（本机）");
    public static string ProviderCustom => T("Custom", "自定义");
    public static string BaseUrlPlaceholder => "https://…/v1";
    public static string KeySavedPlaceholder => T("Saved; leave blank to keep using it", "已保存，留空则继续使用");
    public static string KeyMissingPlaceholder => T("Not saved yet", "尚未保存");
    public static string KeyOtherServicePlaceholder => T("Enter the key for this service", "请填写此服务的密钥");

    /// <summary>The settings point at a key no longer in the system's credential store (for example after restoring a backup).</summary>
    public static string SavedKeyMissing => T(
        "The saved key is no longer in the system’s key store (for example, after restoring an older backup). Enter the key again and save.",
        "保存的密钥已不在系统的密钥存储中（例如恢复了较早的备份）。请重新填写密钥并保存。");
    public static string OllamaKeyPlaceholder => T("Not checked by the service on this computer; enter anything", "本机服务不校验，可填任意内容");
    public static string ConnectionNamePlaceholder => T("Optional", "可选");
    public static string ModelPlaceholder => T("Choose or enter a model name", "选择或输入模型名称");
    public static string ShowModels => T("Show model list", "显示模型列表");
    public static string TokensUnit => T("tokens", "token");
    public static string LocationSection => T("Location", "位置");
    public static string OpenFolder => T("Open", "打开");
    public static string NoBackupsShort => T(
        "None yet. A backup is made automatically before a data upgrade, before a delete that removes other content with it, and before restoring a backup.",
        "暂无。数据升级前、连带删除前和恢复备份前会自动备份。");
    public static string AppearanceSaveFailed => T(
        "The appearance setting couldn’t be saved. It still applies until you quit.",
        "外观设置未能保存，本次运行中仍然有效。");
    public static string LanguageSaveFailed => T(
        "The language setting couldn’t be saved. It still applies until you quit.",
        "语言设置未能保存，本次运行中仍然有效。");

    // A language switch waits while something is under way that a rebuild would cut short (bilingual-ui.md §6.6).
    public static string LanguageSwitchWaiting => T("Switches when the current task finishes…", "当前任务完成后切换…");
    public static string LanguageSwitchFailed => T("Couldn’t switch the language. Try again.", "未能切换语言，请重试。");
    public static string LanguageSwitchNotNow => T(
        "Couldn’t switch the language now. Try again when the current task finishes.",
        "现在无法切换语言，请在当前任务完成后重试。");

    // Hints (2026-11-10): whether the open workspace's conversations pop up a hint too (on unless turned off), and the
    // sound for a hint (off unless chosen).
    public static string HintsSection => T("Notifications and suggestions", "提示");
    public static string NewsInWorkspace => T("Also notify in the current workspace", "当前工作区内也弹出提示");
    public static string NewsInWorkspaceHint => T(
        "Also pops up when a conversation you aren’t viewing gets a new answer. When off, notifications pop up only for other workspaces; the dots in the sidebar show as usual.",
        "没在看的会话有新回答时也弹出；关闭后只为其他工作区弹出，侧栏的圆点照常显示。");
    public static string NewsInWorkspaceSaveFailed => T(
        "The pop-up notification setting couldn’t be saved. It still applies until you quit.",
        "弹出提示设置未能保存，本次运行中仍然有效。");
    public static string NewsSound => T("Play a sound for new answers", "新回答提示音");
    public static string NewsSoundHint => T("Plays only once when several new answers arrive", "有多个新回答时只响一次");
    public static string NewsSoundSaveFailed => T(
        "The notification sound setting couldn’t be saved. It still applies until you quit.",
        "提示音设置未能保存，本次运行中仍然有效。");

    // Names prepared in the background (2026-11-11): on unless turned off.
    public static string PrepareNames => T("Prepare workspace name suggestions automatically", "自动准备工作区名称建议");
    public static string PrepareNamesHint => T(
        $"When a new conversation gets its title, name suggestions for the workspace are prepared in the background and shown right away when you rename it. This sends the model up to {WorkspaceNamePrompt.MaxConversations} conversation titles (the start of the first question for one without a title), up to {WorkspaceNamePrompt.MaxSummaries} summary names and the workspace’s current name, and may cost a little.",
        $"新会话有了标题后，在后台为工作区想好名称，改名时直接显示；会把最多 {WorkspaceNamePrompt.MaxConversations} 个会话标题（没有标题的用第一个问题的开头）、最多 {WorkspaceNamePrompt.MaxSummaries} 个总结名称和工作区现在的名称发给模型，可能产生少量费用。");
    public static string PrepareNamesSaveFailed => T(
        "The name suggestion setting couldn’t be saved. It still applies until you quit.",
        "名称建议设置未能保存，本次运行中仍然有效。");

    public static string SettingsTitle => T("Settings", "设置");

    public static string ModelSection => T("Model connection", "模型连接");
    public static string ConnectionNameLabel => T("Name", "名称");
    public static string BaseUrlLabel => T("Service URL", "服务地址");
    public static string ApiKeyLabel => T("API key", "API 密钥");
    public static string ContextWindowLabel => T("Context window", "上下文容量");
    public static string ContextWindowPlaceholder => T("Leave blank for 32000", "留空按 32000 计");
    public static string ContextWindowHint => T(
        "When a conversation is longer than this, its earlier content is first compressed into a context summary before sending, which means a few more requests, more waiting and more cost. The original record is kept in full.",
        "对话超出时，较早的内容先压缩成摘要再发送，会多几次请求、多些等待与费用；原始记录完整保留。");

    public static string AdvancedSettings => T("Advanced settings", "高级设置");
    public static string ModelCapabilitiesLabel => T("Model capabilities", "模型能力");
    public static string ToolCallsSetting => T("Tool calls", "工具调用");
    public static string ToolCallsHint => T(
        "While answering, the model can ask Mindweave to run the tools it offers (for example, to look something up), then continue its answer with the results.",
        "模型回答时可以请 Mindweave 执行它提供的工具（例如查找资料），拿到结果后再接着回答。");
    public static string ImageInputSetting => T("Image input", "图片输入");
    public static string ImageInputHint => T(
        "You can attach images to a question for the model to look at. Only models that can read images support this.",
        "提问时可以附上图片，由模型看图回答。只有能识图的模型支持。");
    public static string OutputLimitLabel => T("Output limit", "输出上限");
    public static string OutputLimitPlaceholder => T("Service default", "留空由服务决定");
    public static string OutputLimitHint => T(
        "The most tokens each answer may generate. Raise it if answers are often cut off midway; lower it for shorter answers or to control cost.",
        "每次回答最多生成多少 token。回答常在中途被截断时调大；想要更短的回答或控制费用时调小。");
    public static string OutputLimitTip => T(
        "This limit is sent to the model service with every question. If it is blank, nothing is sent and the service uses its own default.\n" +
        "For reasoning models, the thinking usually counts toward the limit too. Set it too low and the model may finish thinking with no room left to answer.\n" +
        "The context window reserves the same amount of room for the answer, so the limit can be at most half the context window.\n" +
        "Internal requests, such as making summaries and compressing context, have their own limits and are not affected by this.",
        "每次提问都会把这个上限发给模型服务；留空则不发送，由服务按它自己的默认值处理。\n" +
        "推理模型的思考过程通常也计入上限，设得太小，可能想完了却来不及作答。\n" +
        "上下文容量里会为回答留出同样多的空间，所以最多只能设为上下文容量的一半。\n" +
        "生成总结、压缩上下文等内部请求有各自的上限，不受这一项影响。");
    public static string ModelCapabilitiesHint => T(
        "Preset for the chosen service and model; if you aren’t sure, leave an option off. Changing the service or model presets them again. If you turn one on that the model doesn’t actually support, the related requests fail.",
        "按所选服务与模型预先填好，拿不准的保持关闭；换了服务或模型会重新填。模型其实不支持时打开，相关请求会失败。");

    public static string ModelLabel => T("Model", "模型");
    public static string FetchModels => T("Get list", "获取列表");
    public static string TestConnection => T("Test connection", "测试连接");
    public static string TestCostHint => T("May cost a little", "可能产生少量费用");
    public static string TestCostTip => T(
        "Sends one very short real request. Getting the model list generates no content.",
        "发出一次很短的真实请求。获取模型列表不生成内容。");
    public static string PrivacyHint => T(
        "Questions and conversation content are sent to this service. The key is stored only in the system Keychain / Credential Manager.",
        "提问与会话内容会发送到此服务；密钥只存于系统钥匙串／凭据管理器。");
    public static string SettingsSaved => T(
        "Saved. New questions will use this configuration; answers in progress are not affected.",
        "已保存。新的提问将使用此配置；正在进行的回答不受影响。");
    public static string NoModelsFound => T(
        "The service returned no models. You can enter a model name yourself.",
        "服务没有返回模型，可以直接手动填写模型名称。");
    public static string TestSucceeded => T("Connection OK.", "连接正常。");

    public static string ModelsFound(int count) => T(
        "Found " + Plural(count, "model", "models") + ".",
        string.Format(CultureInfo.CurrentCulture, "找到 {0} 个模型。", count));

    /// <summary>The failure block's first line for a failed test or model list (D2 §2.1); the service's text is in its details.</summary>
    public static string ConnectionFailed(GenerationErrorKind? kind) => T(
        $"Connection failed: {(kind.HasValue ? ErrorKind(kind.Value) : "unknown error")}",
        $"连接失败：{(kind is { } k ? ErrorKind(k) : "未知错误")}");

    public static string SettingsProblem(ModelSettingsProblem problem) => problem switch
    {
        ModelSettingsProblem.InvalidUrl => T(
            "The service URL is not valid. It needs to be a full address starting with https://.",
            "服务地址无效，需要以 https:// 开头的完整地址。"),
        ModelSettingsProblem.InsecureUrl => T(
            "Only https addresses are allowed (except addresses on this computer), so the key is never sent in plain text.",
            "只允许 https 地址（本机地址除外），以免密钥以明文传输。"),
        ModelSettingsProblem.MissingModel => T("Enter a model name.", "请填写模型名称。"),
        ModelSettingsProblem.MissingKey => T("Enter the API key.", "请填写 API 密钥。"),
        ModelSettingsProblem.CredentialsInUrl => T(
            "The service URL can’t contain a user name or password. Put the key in the “API key” field; it is stored in the system’s key store.",
            "服务地址里不能包含用户名或密码；密钥请填在“API 密钥”一栏，它会存进系统的密钥存储。"),
        ModelSettingsProblem.InvalidOutputLimit => T(
            string.Create(Culture, $"The output limit must be at least {ModelSettingsInput.MinOutputLimit:N0} tokens and no more than half the context window. If you aren’t sure, leave it blank."),
            $"输出上限应不少于 {ModelSettingsInput.MinOutputLimit:N0} token，且不超过上下文容量的一半，不清楚可留空。"),
        ModelSettingsProblem.InvalidContextWindow => T(
            string.Create(Culture, $"The context window must be between {ModelSettingsInput.MinContextWindow:N0} and {ModelSettingsInput.MaxContextWindow:N0} tokens. If you aren’t sure, leave it blank."),
            $"上下文容量应在 {ModelSettingsInput.MinContextWindow:N0} 到 {ModelSettingsInput.MaxContextWindow:N0} token 之间，不清楚可留空。"),
        _ => T("The settings are not valid.", "设置无效。"),
    };

    public static string CredentialUnavailable(string detail) => T(
        $"The system credential service is unavailable. Unlock it and try again ({detail}). The key will not be saved in plain text instead.",
        $"系统凭据服务不可用，请解锁后重试（{detail}）。密钥不会改为明文保存。");
}
