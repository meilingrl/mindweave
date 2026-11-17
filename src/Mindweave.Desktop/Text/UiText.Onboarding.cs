using System.Globalization;

namespace Mindweave.Desktop.Text;

// First-run onboarding (首次引导, requirements §6.1 item 1, 2026-11-17): what Mindweave is for, connecting a model, the
// first 主题工作区; and the note on 新探索 while no model is connected.
public static partial class UiText
{
    public const int OnboardingStepCount = 3;

    /// <summary>"第 1 步，共 3 步".</summary>
    public static string OnboardingStepOf(int step) =>
        string.Format(CultureInfo.CurrentCulture, "第 {0} 步，共 {1} 步", step, OnboardingStepCount);

    public const string SkipOnboarding = "跳过引导";

    public const string SkipOnboardingTip = "直接创建工作区；模型可以之后在设置里连接";

    public const string OnboardingNext = "下一步";

    public const string OnboardingBack = "上一步";

    // 1 · What Mindweave is for
    public const string OnboardingHeading = "欢迎使用 Mindweave";

    public const string OnboardingWelcomeBody = "围绕一个主题和 AI 边问边读。读到想深究的地方，选中文字创建分支会话；问清楚后返回来源接着读；随时把收获整理成总结，连成这个主题的总结网络。";

    public const string OnboardingIllustrationName = "示意：从回答中创建分支，返回来源，整理成总结网络";

    public const string OnboardingIllustrationBranch = "选中文字，创建分支";

    public const string OnboardingIllustrationReturn = "问清楚，返回来源";

    public const string OnboardingIllustrationNetwork = "整理成总结网络";

    // 2 · A model
    public const string OnboardingModelHeading = "连接一个模型";

    public const string OnboardingModelBody = "回答由你自己的模型服务生成。选一个服务，填入 API 密钥，再选一个模型。";

    public const string SaveAndContinue = "保存并继续";

    public const string ConnectModelLater = "稍后设置";

    public const string ConnectModelLaterTip = "之后可以在 设置 › 模型连接 里设置；在此之前不能提问";

    // 3 · The first workspace
    public const string OnboardingWorkspaceHeading = "建一个主题工作区";

    public const string OnboardingWorkspaceBody = "一个工作区收拢一个主题：这个主题下的会话、总结和总结网络都放在里面，不同主题互不混杂。";

    public const string WorkspaceExamplesLabel = "可以从这些开始：";

    /// <summary>Example names offered as chips; a click puts one in the box.</summary>
    public static IReadOnlyList<string> WorkspaceExamples { get; } = ["火车历史", "机器学习入门", "咖啡烘焙"];

    public static string UseWorkspaceExampleName(string name) => $"用“{name}”作名称";

    // 新探索 while no model is connected
    public const string ModelNotConnectedNote = "还没有连接模型，连接后才能提问。";
}
