using System.Reflection;
using System.Runtime.ExceptionServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Languages;
using Mindweave.Application.Recommendations;
using Mindweave.Core.Conversations;
using Mindweave.Core.Runs;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Pages;
using static Mindweave.Desktop.Tests.UiTree;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// A switch that cannot be made, or not yet (bilingual-ui.md §6.6, review of merge 2): a rebuild that fails leaves the
/// old view, language and stored choice as they were and says so; a switch that waits says so, gives up after a while,
/// and is dropped when the reader leaves the picker; work the old view started finishes harmlessly; replayed pages
/// carry what lives only in them and start nothing the reader did not ask for.
/// </summary>
public partial class LanguageSwitchTests
{
    /// <summary>Review I1: a replayed page cannot be built.</summary>
    [AvaloniaFact]
    public async Task Failed_rebuild_keeps_the_old_view_language_and_choice()
    {
        try
        {
            var pages = new FailingPages();
            await using var app = DesktopHarness.Create(s => s.AddSingleton<IPageFactory>(pages.Wrap));
            var window = app.ShowWindow(1000, 720);
            await app.StartWithWorkspaceAsync("火车");
            await app.ConfigureModelAsync();
            await ConversationAsync(app, window, "火车是怎么发明的？", "蒸汽机车。");
            app.ShellPage.ShowSectionCommand.Execute(MainSection.SummaryNetwork);
            await Assert.IsType<SummaryNetworkHomeViewModel>(app.Workbench.CurrentPage).Loaded;
            app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
            var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
            await settings.Loaded;
            window.UpdateLayout();
            var view = app.Services;
            var choice = view.GetRequiredService<ILanguageChoice>();
            var counts = ViewRebuildTests.HandlerCounts(app.Session.Root);

            // Settings is the last section replayed: the explore and network pages are built in the new view first.
            pages.Fail = route => route is SettingsHomeRoute;
            UiInput.Click(window, Find<RadioButton>(window, "LanguageEnglish"));
            await Switch(app).Running;

            Assert.Equal(1, pages.Failed);
            Assert.Equal(UiLanguage.Chinese, CurrentLanguage.Value);
            Assert.Same(view, app.Services);
            Assert.Same(settings, app.Workbench.CurrentPage);
            Assert.Equal(0, Switch(app).Rebuilds);

            // The choice was written before the rebuild; the old one is written back, so file and screen agree.
            Assert.Equal(UiLanguage.Chinese, await choice.ReadAsync());

            // Nothing the new view made is left listening to the services.
            ViewRebuildTests.AssertSame(counts, ViewRebuildTests.HandlerCounts(app.Session.Root));

            // The old view is in the window again, in Chinese, with a notice beside the picker.
            window.UpdateLayout();
            Assert.Same(app.Root, Assert.IsType<RootView>(window.Content).DataContext);
            Assert.Same(settings, window.GetVisualDescendants().OfType<SettingsHomeView>().Single().DataContext);
            Assert.True(Find<RadioButton>(window, "LanguageChinese").IsChecked);
            Assert.Equal(UiText.LanguageSwitchFailed, settings.LanguageStatus);
            var status = Find<TextBlock>(window, "LanguageStatus");
            Assert.True(status.IsEffectivelyVisible);
            Assert.Equal("未能切换语言，请重试。", status.Text);

            // The next choice switches.
            pages.Fail = null;
            UiInput.Click(window, Find<RadioButton>(window, "LanguageEnglish"));
            await Switch(app).Running;
            Assert.Equal(UiLanguage.English, CurrentLanguage.Value);
            Assert.Equal(UiLanguage.English, await choice.ReadAsync());
            var again = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
            Assert.NotSame(settings, again);
            Assert.Null(again.LanguageStatus);
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    /// <summary>Review I1 (b): the window cannot let go of the old root view; it shows it again, nothing is lost.</summary>
    [AvaloniaFact]
    public async Task Failed_detach_shows_the_old_view_again()
    {
        try
        {
            await using var app = DesktopHarness.Create();
            var window = app.ShowWindow(1000, 720);
            await app.StartWithWorkspaceAsync("火车");
            var settings = await OpenSettingsAsync(app, window);
            var view = app.Services;
            var fail = true;
            app.Session.ViewDetaching += (_, _) =>
            {
                if (fail)
                {
                    throw new InvalidOperationException("The window cannot let go (test).");
                }
            };

            var switching = SwitchAsync(app, UiLanguage.English);
            await switching;

            Assert.True(switching.IsCompletedSuccessfully);
            Assert.Equal(UiLanguage.Chinese, CurrentLanguage.Value);
            Assert.Same(view, app.Services);
            window.UpdateLayout();
            Assert.Same(app.Root, Assert.IsType<RootView>(window.Content).DataContext);
            Assert.Same(settings, window.GetVisualDescendants().OfType<SettingsHomeView>().Single().DataContext);
            Assert.Equal(UiText.LanguageSwitchFailed, settings.LanguageStatus);

            fail = false;
            await SwitchAsync(app, UiLanguage.English);
            Assert.Equal(UiLanguage.English, CurrentLanguage.Value);
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    /// <summary>Review M1: while something holds the switch, the picker says it will switch; then it does.</summary>
    [AvaloniaFact]
    public async Task Waiting_switch_says_so_then_switches()
    {
        try
        {
            await using var app = DesktopHarness.Create();
            var window = app.ShowWindow(1000, 720);
            var workspace = await app.StartWithWorkspaceAsync("火车");
            var settings = await OpenSettingsAsync(app, window);
            app.Root.WorkspaceDeletion.Open(workspace);
            await app.Root.WorkspaceDeletion.Loading;

            settings.IsEnglish = true;
            await UiWait.UntilAsync(() => Switch(app).IsWaiting);
            Assert.Equal(UiText.LanguageSwitchWaiting, settings.LanguageStatus);
            window.UpdateLayout();
            var status = Find<TextBlock>(window, "LanguageStatus");
            Assert.True(status.IsEffectivelyVisible);
            Assert.Equal("当前任务完成后切换…", status.Text);
            Assert.DoesNotContain("error", status.Classes);
            Assert.Equal(UiLanguage.Chinese, CurrentLanguage.Value);

            app.Root.WorkspaceDeletion.CancelCommand.Execute(null);
            await Switch(app).Running;

            Assert.Equal(UiLanguage.English, CurrentLanguage.Value);
            var again = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
            Assert.Null(again.LanguageStatus);
            Assert.False(Switch(app).IsWaiting);
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    /// <summary>Review M1: a switch held too long gives up, keeps the language and the stored choice, and says so.</summary>
    [AvaloniaFact]
    public async Task Waiting_switch_gives_up_after_a_while()
    {
        var limit = LanguageSwitch.WaitLimit;
        try
        {
            LanguageSwitch.WaitLimit = TimeSpan.FromMilliseconds(400);
            var choice = new LanguageChoiceUiTests.ScriptedChoice { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
            await using var app = DesktopHarness.Create(s => s.AddSingleton<ILanguageChoice>(choice));
            var window = app.ShowWindow(1000, 720);
            var workspace = await app.StartWithWorkspaceAsync("火车");
            var settings = await OpenSettingsAsync(app, window);

            // The choice is being written when a dialog opens: the rebuild waits for the dialog, which stays open.
            settings.IsEnglish = true;
            await UiWait.UntilAsync(() => choice.Writes.Count == 1);
            app.Root.WorkspaceDeletion.Open(workspace);
            await app.Root.WorkspaceDeletion.Loading;
            choice.Gate.TrySetResult();
            await Switch(app).Running;

            Assert.Equal(UiLanguage.Chinese, CurrentLanguage.Value);
            Assert.Equal(0, Switch(app).Rebuilds);
            Assert.Equal([UiLanguage.English, UiLanguage.Chinese], choice.Writes);
            Assert.Equal(UiLanguage.Chinese, choice.Stored);
            Assert.False(Switch(app).IsWaiting);
            Assert.Same(settings, app.Workbench.CurrentPage);
            Assert.True(settings.IsChinese);
            Assert.Equal(UiText.LanguageSwitchNotNow, settings.LanguageStatus);

            // Once the dialog is closed, the choice can be made again.
            app.Root.WorkspaceDeletion.CancelCommand.Execute(null);
            settings.IsEnglish = true;
            await Switch(app).Running;
            Assert.Equal(UiLanguage.English, CurrentLanguage.Value);
            window.Close();
        }
        finally
        {
            LanguageSwitch.WaitLimit = limit;
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    /// <summary>Review M1: leaving Settings while the switch waits drops it; the picker shows the language in use.</summary>
    [AvaloniaFact]
    public async Task Leaving_settings_drops_a_waiting_switch()
    {
        try
        {
            await using var app = DesktopHarness.Create();
            var window = app.ShowWindow(1000, 720);
            var workspace = await app.StartWithWorkspaceAsync("火车");
            var settings = await OpenSettingsAsync(app, window);
            var told = new List<string?>();
            settings.PropertyChanged += (_, e) => told.Add(e.PropertyName);
            app.Root.WorkspaceDeletion.Open(workspace);
            await app.Root.WorkspaceDeletion.Loading;
            settings.IsEnglish = true;
            await UiWait.UntilAsync(() => Switch(app).IsWaiting);

            app.ShellPage.ShowSectionCommand.Execute(MainSection.Explore);
            await Switch(app).Running;

            Assert.Equal(UiLanguage.Chinese, CurrentLanguage.Value);
            Assert.Equal(0, Switch(app).Rebuilds);
            Assert.False(Switch(app).IsWaiting);
            Assert.False(File.Exists(Path.Combine(app.Paths.DataDirectory, "language")));
            Assert.Contains(nameof(SettingsHomeViewModel.IsChinese), told);
            Assert.True(settings.IsChinese);
            Assert.Null(settings.LanguageStatus);

            // The dialog closes later: nothing switches by itself.
            app.Root.WorkspaceDeletion.CancelCommand.Execute(null);
            await Task.Delay(LanguageSwitch.SettleInterval * 4);
            Assert.Equal(UiLanguage.Chinese, CurrentLanguage.Value);
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    /// <summary>
    /// Review M2: a switch chosen while a first question is being sent waits for it, so the new view shows the
    /// conversation it started; the released workbench ignores navigation that comes late.
    /// </summary>
    [AvaloniaFact]
    public async Task Switch_waits_for_a_send_and_shows_the_started_conversation()
    {
        try
        {
            var held = new HeldStart();
            await using var app = DesktopHarness.Create(s => s.AddSingleton<IConversations>(sp => held.Wrap(sp.GetRequiredService<ConversationService>())));
            var window = app.ShowWindow(1000, 720);
            await app.StartWithWorkspaceAsync("火车");
            await app.ConfigureModelAsync();
            app.Client.Reply("蒸汽机车。");
            app.ShellPage.Conversations.NewCommand.Execute(null);
            var fresh = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
            await fresh.DraftLoaded;
            fresh.Composer.Text = "火车是怎么发明的？";
            held.Hold();
            var sending = fresh.Composer.SendCommand.ExecuteAsync(null);
            await UiWait.UntilAsync(() => held.IsHolding);
            var old = app.Workbench;

            var switching = SwitchAsync(app, UiLanguage.English);
            await UiWait.UntilAsync(() => Switch(app).IsWaiting);
            Assert.Equal(0, Switch(app).Rebuilds);

            held.Release();
            await sending;
            await switching;

            Assert.Equal(UiLanguage.English, CurrentLanguage.Value);
            var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
            Assert.Equal(held.Started, page.Id);
            await page.Loaded;
            Assert.Equal("火车是怎么发明的？", page.Messages[0].Body);

            // Whatever the old pages still do reaches a workbench that has let them go: nothing happens.
            old.Replace(new ConversationRoute(page.Id));
            old.Push(NewConversationRoute.Instance);
            old.Navigate(new NavigationRequest(MainSection.Settings));
            old.GoBack();
            old.SwitchWorkspace(app.Workbench.CurrentWorkspace!.Id);
            Assert.Null(old.CurrentPage);
            Assert.Same(page, app.Workbench.CurrentPage);
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    /// <summary>Review M3: the composer's web search switch keeps the position the reader chose.</summary>
    [AvaloniaFact]
    public async Task Switch_keeps_the_web_search_choice()
    {
        try
        {
            await using var app = DesktopHarness.Create();
            var window = app.ShowWindow(1000, 720);
            await StartWithToolsAsync(app);
            var conversation = await ConversationAsync(app, window, "火车是怎么发明的？", "蒸汽机车。");
            await conversation.Composer.WebSearch!.Loaded;
            conversation.Composer.WebSearch.ChooseCommand.Execute(WebSearchMode.Off);
            app.ShellPage.Conversations.NewCommand.Execute(null);
            var fresh = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
            await fresh.Composer.WebSearch!.Loaded;
            fresh.Composer.WebSearch.ChooseCommand.Execute(WebSearchMode.On);
            Assert.NotEqual(WebSearchMode.On, (await app.Services.GetRequiredService<Application.Preferences.IWebSearchSettings>().GetAsync(CancellationToken.None)).DefaultMode);

            await SwitchAsync(app, UiLanguage.English);

            var freshAgain = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
            Assert.NotSame(fresh, freshAgain);
            await freshAgain.Composer.WebSearch!.Loaded;
            Assert.Equal(WebSearchMode.On, freshAgain.Composer.WebSearch.Mode);
            app.ShellPage.GoBackCommand.Execute(null);
            var conversationAgain = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
            Assert.NotSame(conversation, conversationAgain);
            await conversationAgain.Composer.WebSearch!.Loaded;
            Assert.Equal(WebSearchMode.Off, conversationAgain.Composer.WebSearch.Mode);
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    /// <summary>
    /// Review M4: a replayed page is not opened again. New exploration in the active workspace and in another one, and
    /// the summary network, ask the recommendations nothing on a switch (opening them may start a paid request).
    /// </summary>
    [AvaloniaFact]
    public async Task Switch_starts_no_recommendation_request()
    {
        try
        {
            var counted = new CountedRecommendations();
            await using var app = DesktopHarness.Create(s => s.AddSingleton<IRecommendations>(sp => counted.Wrap(sp.GetRequiredService<RecommendationService>())));
            var window = app.ShowWindow(1000, 720);
            await app.StartWithWorkspaceAsync("火车");
            app.ShellPage.Conversations.NewCommand.Execute(null);
            Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
            app.ShellPage.ShowSectionCommand.Execute(MainSection.SummaryNetwork);
            await Assert.IsType<SummaryNetworkHomeViewModel>(app.Workbench.CurrentPage).Loaded;
            app.ShellPage.NewWorkspace.Name = "航空";
            await app.ShellPage.NewWorkspace.SubmitCommand.ExecuteAsync(null);
            Assert.Equal(2, app.Workbench.OpenWorkspaces.Count);
            if (app.Workbench.CurrentPage is not NewConversationViewModel)
            {
                app.ShellPage.Conversations.NewCommand.Execute(null);
            }

            Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
            Assert.True(counted.Opened > 0);
            var before = (counted.Opened, counted.NetworkOpened);

            await SwitchAsync(app, UiLanguage.English);
            await Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage).Loaded;
            var network = app.Workbench.AllPages().OfType<SummaryNetworkHomeViewModel>().Single();
            await network.Loaded;
            await network.RecommendationsReloaded;

            Assert.Equal(before, (counted.Opened, counted.NetworkOpened));

            // Opening them anew still counts as opening.
            app.ShellPage.GoBackCommand.Execute(null);
            app.ShellPage.Conversations.NewCommand.Execute(null);
            Assert.Equal(before.Opened + 1, counted.Opened);
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    /// <summary>
    /// Review M6 (1): a real switch, English to Chinese, with a conversation, the summary network and Settings alive,
    /// leaves every delegate of every root service with the handlers it had.
    /// </summary>
    [AvaloniaFact]
    public async Task Switch_leaves_root_handler_counts_unchanged()
    {
        try
        {
            CurrentLanguage.Set(UiLanguage.English);
            await using var app = DesktopHarness.Create();
            var window = app.ShowWindow(1000, 720);
            await app.StartWithWorkspaceAsync("Trains");
            await app.ConfigureModelAsync();
            await ConversationAsync(app, window, "How were trains invented?", "Steam.");
            app.ShellPage.ShowSectionCommand.Execute(MainSection.SummaryNetwork);
            await Assert.IsType<SummaryNetworkHomeViewModel>(app.Workbench.CurrentPage).Loaded;
            var settings = await OpenSettingsAsync(app, window);
            await settings.Model.Loaded;
            var before = ViewRebuildTests.HandlerCounts(app.Session.Root);

            UiInput.Click(window, Find<RadioButton>(window, "LanguageChinese"));
            await Switch(app).Running;

            Assert.Equal(UiLanguage.Chinese, CurrentLanguage.Value);
            Assert.Equal(1, Switch(app).Rebuilds);
            var again = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
            await again.Loaded;
            await again.Model.Loaded;
            window.UpdateLayout();
            ViewRebuildTests.AssertSame(before, ViewRebuildTests.HandlerCounts(app.Session.Root));
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    private static LanguageSwitch Switch(DesktopHarness app) => app.Services.GetRequiredService<LanguageSwitch>();

    private static async Task<SettingsHomeViewModel> OpenSettingsAsync(DesktopHarness app, Window window)
    {
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await settings.Loaded;
        window.UpdateLayout();
        return settings;
    }

    /// <summary>The real pages, except that a route <see cref="Fail"/> picks cannot be built.</summary>
    private sealed class FailingPages
    {
        public Func<ContentRoute, bool>? Fail { get; set; }

        public int Failed { get; private set; }

        public IPageFactory Wrap(IServiceProvider services) => new Factory(this, ActivatorUtilities.CreateInstance<PageFactory>(services));

        private sealed class Factory(FailingPages owner, IPageFactory inner) : IPageFactory
        {
            public PageViewModel Create(ContentRoute route, PageContext context)
            {
                if (owner.Fail?.Invoke(route) == true)
                {
                    owner.Failed++;
                    throw new InvalidOperationException("A page that cannot be built (test).");
                }

                return inner.Create(route, context);
            }
        }
    }

    /// <summary>Holds the answer to a first question (the conversation is made; the page hears of it on release).</summary>
    private sealed class HeldStart
    {
        private TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _armed;

        public bool IsHolding { get; private set; }

        public ConversationId? Started { get; private set; }

        public void Hold() => _armed = true;

        public void Release()
        {
            _armed = false;
            _gate.TrySetResult();
        }

        public IConversations Wrap(IConversations inner)
        {
            var proxy = DispatchProxy.Create<IConversations, Proxy>();
            ((Proxy)(object)proxy).Init(inner, this);
            return proxy;
        }

        private async Task<SendOutcome> Start(Task<SendOutcome> start)
        {
            var outcome = await start;
            Started = outcome.Conversation;
            if (_armed)
            {
                IsHolding = true;
                await _gate.Task;
                IsHolding = false;
            }

            return outcome;
        }

        // DispatchProxy needs a public, unsealed type with a parameterless constructor.
#pragma warning disable CA1812, CA1852
        public class Proxy : DispatchProxy
#pragma warning restore CA1812, CA1852
        {
            private IConversations _inner = null!;
            private HeldStart _owner = null!;

            internal void Init(IConversations inner, HeldStart owner)
            {
                _inner = inner;
                _owner = owner;
            }

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                object? result;
                try
                {
                    result = targetMethod!.Invoke(_inner, args);
                }
                catch (TargetInvocationException ex) when (ex.InnerException is not null)
                {
                    ExceptionDispatchInfo.Throw(ex.InnerException);
                    throw;
                }

                return targetMethod.Name == nameof(IConversations.StartAsync) ? _owner.Start((Task<SendOutcome>)result!) : result;
            }
        }
    }

    /// <summary>Counts what the pages tell the recommendations about being opened.</summary>
    private sealed class CountedRecommendations
    {
        public int Opened;

        public int NetworkOpened;

        public IRecommendations Wrap(IRecommendations inner)
        {
            var proxy = DispatchProxy.Create<IRecommendations, Proxy>();
            ((Proxy)(object)proxy).Init(inner, this);
            return proxy;
        }

        // DispatchProxy needs a public, unsealed type with a parameterless constructor.
#pragma warning disable CA1812, CA1852
        public class Proxy : DispatchProxy
#pragma warning restore CA1812, CA1852
        {
            private IRecommendations _inner = null!;
            private CountedRecommendations _owner = null!;

            internal void Init(IRecommendations inner, CountedRecommendations owner)
            {
                _inner = inner;
                _owner = owner;
            }

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                switch (targetMethod!.Name)
                {
                    case nameof(IRecommendations.NewExplorationOpened):
                        Interlocked.Increment(ref _owner.Opened);
                        break;
                    case nameof(IRecommendations.SummaryNetworkOpened):
                        Interlocked.Increment(ref _owner.NetworkOpened);
                        break;
                }

                try
                {
                    return targetMethod.Invoke(_inner, args);
                }
                catch (TargetInvocationException ex) when (ex.InnerException is not null)
                {
                    ExceptionDispatchInfo.Throw(ex.InnerException);
                    throw;
                }
            }
        }
    }
}
