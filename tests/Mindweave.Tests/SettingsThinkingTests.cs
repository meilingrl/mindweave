using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Text.Json;
using Mindweave.Application.Chat;
using Mindweave.Core.Chat;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.Views;
using Mindweave.Infrastructure.Storage;
using Mindweave.Infrastructure.Runtime;
using Xunit;
using F = Mindweave.Tests.GenerationCoordinatorTests.Fixture;

namespace Mindweave.Tests;

public sealed class SettingsThinkingTests
{
    [AvaloniaFact]
    public async Task First_configuration_starts_at_service_default_and_only_explicit_save_writes_new_credential()
    {
        await using var f = await F.OpenAsync(); f.Sql("DELETE FROM active_ai_configuration");
        var vault = new RecordingVault(); var settings = await Open(f, vault);
        var view = Mount(settings, out var window);
        try
        {
            Assert.Equal(0, Selector(view).SelectedIndex); Assert.Null(await f.Store.LoadActiveConfigurationAsync());
            AssertNoSecretOrRequest(vault, f);
            Selector(view).SelectedIndex = 1;
            view.FindControl<TextBox>("SecretInput")!.Text = "synthetic-first-secret"; Dispatcher.UIThread.RunJobs();
            AssertNoSecretOrRequest(vault, f);
            await Save(view);
            Assert.Null(settings.ErrorMessage); Assert.Equal("", settings.Secret);
            var active = (await f.Store.LoadActiveConfigurationAsync())!;
            Assert.Equal(SummaryThinkingMode.Disabled, active.SummaryThinking);
            Assert.NotEqual(Guid.Empty, active.ConnectionId); Assert.NotEqual(Guid.Empty, active.ModelConfigId);
            Assert.Equal("synthetic-new-version", active.CredentialReference);
            Assert.Equal(1, vault.Writes); Assert.Equal(0, vault.Reads); Assert.Equal(0, vault.Deletes);
            Assert.Equal(0, f.Transport.StartCount); Assert.Null(f.Transport.DiscoverySecret);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Legacy_configuration_opens_bound_default_and_only_explicit_save_persists_disabled()
    {
        await using var f = await F.OpenAsync();
        var original = f.Configuration with { BaseUrl = "https://api.deepseek.com/v1", TokenLimitParameter = "max_completion_tokens", MaxOutputTokens = 4096 };
        await f.Store.SaveConfigurationAsync(original);
        Assert.DoesNotContain("SummaryThinking", (string)ContextStorageTests.Sql(f.Paths, "SELECT configuration_json FROM active_ai_configuration")!);
        var vault = new RecordingVault();
        var settings = await Open(f, vault);
        var view = Mount(settings, out var window);
        try
        {
            var selector = Selector(view);
            Assert.Equal("摘要思考模式", AutomationProperties.GetName(selector));
            Assert.Equal(new[] { "服务默认", "关闭（仅DeepSeek官方摘要）" }, selector.Items.Cast<object>().Select(item => item.ToString()));
            Assert.Equal(0, selector.SelectedIndex);
            Assert.Contains("摘要思考模式", Texts(view));
            Assert.Contains(Texts(view), text => text.Contains("仅影响长历史摘要", StringComparison.Ordinal) && text.Contains("普通回答和自动标题沿用服务默认", StringComparison.Ordinal) && text.Contains("DeepSeek官方", StringComparison.Ordinal));
            Assert.Contains("服务默认", Texts(view));
            Assert.Equal('●', view.FindControl<TextBox>("SecretInput")!.PasswordChar);

            selector.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            Assert.Equal(original, await f.Store.LoadActiveConfigurationAsync());
            AssertNoSecretOrRequest(vault, f);
            await Save(view);
            Assert.Null(settings.ErrorMessage); Assert.Equal("", settings.Secret);
            var expected = original with { SummaryThinking = SummaryThinkingMode.Disabled };
            Assert.Equal(expected, await f.Store.LoadActiveConfigurationAsync());
            AssertNoSecretOrRequest(vault, f);

            await using var reopened = await SqliteWorkspaceStore.OpenAsync(f.Paths);
            var restored = new SettingsViewModel(reopened, new(reopened, vault, f.Transport), () => false);
            await restored.InitializeAsync(); view.DataContext = restored; Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, selector.SelectedIndex); Assert.Contains("关闭（仅DeepSeek官方摘要）", Texts(view));
            Assert.Equal(expected, await reopened.LoadActiveConfigurationAsync());
            // A different settings edit must retain the explicitly stored mode and IDs/reference.
            Field(view, "模型 ID").Text = "edited-model"; Dispatcher.UIThread.RunJobs();
            await Save(view);
            Assert.Equal(expected with { ModelId = "edited-model" }, await reopened.LoadActiveConfigurationAsync());
            selector.SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
            Assert.Equal(SummaryThinkingMode.Disabled, (await reopened.LoadActiveConfigurationAsync())!.SummaryThinking);
            AssertNoSecretOrRequest(vault, f);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Editing_existing_disabled_configuration_does_not_reset_policy_or_credential_version()
    {
        await using var f = await F.OpenAsync();
        var original = f.Configuration with { BaseUrl = "https://api.deepseek.com", SummaryThinking = SummaryThinkingMode.Disabled };
        await f.Store.SaveConfigurationAsync(original);
        var vault = new RecordingVault(); var settings = await Open(f, vault);
        settings.ConnectionName = "edited connection"; settings.ContextTokenBudget = 24000;
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.Equal(original with { Name = "edited connection", ContextTokenBudget = 24000 }, await f.Store.LoadActiveConfigurationAsync());
        Assert.Null(settings.ErrorMessage); AssertNoSecretOrRequest(vault, f);
    }

    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task Invalid_selection_or_endpoint_keeps_all_inputs_active_configuration_and_persistence_gate(bool invalidEndpoint)
    {
        await using var f = await F.OpenAsync(); var vault = new RecordingVault(); var paused = false;
        var settings = await Open(f, vault, () => paused, () => paused = true);
        var original = await f.Store.LoadActiveConfigurationAsync();
        var view = Mount(settings, out var window);
        try
        {
            var selector = Selector(view);
            selector.SelectedIndex = invalidEndpoint ? 1 : -1;
            Field(view, "服务地址").Text = "https://compatible.example/v1";
            Field(view, "模型 ID").Text = "keep-model";
            view.FindControl<TextBox>("SecretInput")!.Text = "synthetic-unsaved-input";
            Dispatcher.UIThread.RunJobs();
            settings.ContextTokenBudget = 18000;
            await Save(view);
            Assert.NotNull(settings.ErrorMessage);
            Assert.Contains(invalidEndpoint ? "DeepSeek" : "选择", settings.ErrorMessage);
            Assert.Equal("synthetic-unsaved-input", settings.Secret); Assert.Equal("keep-model", settings.ModelId);
            Assert.Equal("https://compatible.example/v1", settings.BaseUrl); Assert.Equal(18000, settings.ContextTokenBudget);
            Assert.Equal(invalidEndpoint ? 1 : -1, selector.SelectedIndex);
            Assert.Equal(original, await f.Store.LoadActiveConfigurationAsync());
            Assert.False(paused); Assert.True(settings.CanSave); Assert.True(settings.CanDiscover);
            AssertNoSecretOrRequest(vault, f);
            await Discover(view);
            Assert.NotNull(settings.ErrorMessage); AssertNoSecretOrRequest(vault, f);
            view.FindControl<TextBox>("SecretInput")!.Text = ""; Dispatcher.UIThread.RunJobs();
            await Discover(view);
            Assert.Contains(invalidEndpoint ? "DeepSeek" : "选择", settings.ErrorMessage);
            AssertNoSecretOrRequest(vault, f);
            // Both allowed recovery routes work without restarting or clearing the user's input.
            if (invalidEndpoint) Field(view, "服务地址").Text = "https://api.deepseek.com/v1";
            else selector.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            await Save(view);
            Assert.Null(settings.ErrorMessage); Assert.False(paused);
            var active = (await f.Store.LoadActiveConfigurationAsync())!;
            Assert.Equal(invalidEndpoint ? SummaryThinkingMode.Disabled : SummaryThinkingMode.ServiceDefault, active.SummaryThinking);
            Assert.Equal(original!.CredentialReference, active.CredentialReference);
            Assert.Equal("keep-model", active.ModelId); AssertNoSecretOrRequest(vault, f);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Selector_respects_busy_state_and_existing_save_gate_without_implicit_discovery()
    {
        await using var f = await F.OpenAsync();
        await f.Store.SaveConfigurationAsync(f.Configuration with { BaseUrl = "https://api.deepseek.com" });
        var blocked = false; var vault = new RecordingVault { PendingWrite = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var settings = await Open(f, vault, () => blocked);
        var view = Mount(settings, out var window);
        try
        {
            var selector = Selector(view); selector.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            blocked = true; settings.RefreshAvailability(); Dispatcher.UIThread.RunJobs();
            Assert.False(Button(view, "保存配置").IsEnabled); Assert.False(Button(view, "获取模型列表").IsEnabled);
            await settings.SaveCommand.ExecuteAsync(null); await settings.DiscoverCommand.ExecuteAsync(null);
            AssertNoSecretOrRequest(vault, f);
            blocked = false; settings.RefreshAvailability(); settings.Secret = "synthetic-replacement";
            var save = Save(view); Dispatcher.UIThread.RunJobs();
            Assert.True(settings.IsBusy); Assert.False(selector.IsEnabled);
            Assert.False(Button(view, "保存配置").IsEnabled); Assert.False(Button(view, "获取模型列表").IsEnabled);
            Assert.True(view.FindControl<TextBox>("SecretInput")!.IsReadOnly);
            Assert.Equal(1, vault.Writes); Assert.Equal(0, vault.Reads); Assert.Null(f.Transport.DiscoverySecret); Assert.Equal(0, f.Transport.StartCount);
            vault.PendingWrite.SetResult("synthetic-new-version"); await save; Dispatcher.UIThread.RunJobs();
            Assert.False(settings.IsBusy); Assert.True(selector.IsEnabled); Assert.True(Button(view, "保存配置").IsEnabled);
            Assert.Equal("", settings.Secret); Assert.Equal("synthetic-new-version", (await f.Store.LoadActiveConfigurationAsync())!.CredentialReference);
            Assert.Equal(SummaryThinkingMode.Disabled, (await f.Store.LoadActiveConfigurationAsync())!.SummaryThinking);
            await Discover(view);
            Assert.Equal(1, vault.Reads); Assert.Equal("synthetic-only", f.Transport.DiscoverySecret); Assert.Equal(new[] { "model-a" }, settings.Models);
            Assert.Equal(0, f.Transport.StartCount);
        }
        finally { vault.PendingWrite.TrySetResult("synthetic-new-version"); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Unknown_mode_is_rejected_before_vault_and_never_silently_becomes_default()
    {
        await using var f = await F.OpenAsync(); var vault = new RecordingVault(); var paused = false;
        var settings = await Open(f, vault, () => paused, () => paused = true);
        var original = await f.Store.LoadActiveConfigurationAsync();
        settings.SelectedSummaryThinking = new((SummaryThinkingMode)99, "synthetic unknown choice");
        settings.Secret = "synthetic-unsaved-input";
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.Contains("未知摘要思考策略", settings.ErrorMessage);
        Assert.Equal((SummaryThinkingMode)99, settings.SelectedSummaryThinking.Mode);
        Assert.Equal("synthetic-unsaved-input", settings.Secret);
        Assert.Equal(original, await f.Store.LoadActiveConfigurationAsync());
        await settings.DiscoverCommand.ExecuteAsync(null);
        Assert.Contains("未知摘要思考策略", settings.ErrorMessage);
        Assert.False(paused); AssertNoSecretOrRequest(vault, f);
    }

    [AvaloniaTheory]
    [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task Saving_new_mode_keeps_old_history_cache_notice_and_frozen_request_readable(int version)
    {
        using var directory = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", directory.Path);
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", $"context-format{version}");
        Directory.CreateDirectory(paths.DataDirectory); File.Copy(Path.Combine(fixture, $"format{version}.db"), paths.DatabasePath);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture, "manifest.json")));
        var taskId = manifest.RootElement.GetProperty("TaskId").GetGuid();
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths);
        var original = manifest.RootElement.GetProperty("Configuration").Deserialize<AiConfiguration>()!;
        await store.SaveConfigurationAsync(original);
        var generation = (await store.LoadGenerationAsync(taskId))!;
        var oldPlan = JsonSerializer.Serialize(await store.LoadContextPlanAsync(taskId));
        var oldPrepared = JsonSerializer.Serialize(await store.LoadPreparedReplyRequestAsync(taskId));
        var oldMessages = JsonSerializer.Serialize(await store.ReadMessagesAsync(generation.ConversationId));
        var cacheKeys = manifest.RootElement.GetProperty("Chunks").Deserialize<ContextChunk[]>()!.Select(chunk => chunk.CacheKey).ToArray();
        var oldCaches = new List<string>();
        foreach (var key in cacheKeys) oldCaches.Add(JsonSerializer.Serialize(await store.LoadCompletedSummaryAsync(taskId, key)));
        var vault = new RecordingVault(); var transport = new GenerationCoordinatorTests.ControlledTransport();
        await using var coordinator = new GenerationCoordinator(store, transport, vault);
        var settings = new SettingsViewModel(store, new(store, vault, transport), () => false); await settings.InitializeAsync();
        var view = Mount(settings, out var window);
        ChatViewModel? history = null;
        try
        {
            Field(view, "服务地址").Text = "https://api.deepseek.com";
            Selector(view).SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            await Save(view); Assert.Null(settings.ErrorMessage);
            Assert.Equal(original with { BaseUrl = "https://api.deepseek.com", SummaryThinking = SummaryThinkingMode.Disabled }, await store.LoadActiveConfigurationAsync());
            Assert.Equal(oldPlan, JsonSerializer.Serialize(await store.LoadContextPlanAsync(taskId)));
            Assert.Equal(oldPrepared, JsonSerializer.Serialize(await store.LoadPreparedReplyRequestAsync(taskId)));
            Assert.Equal(oldMessages, JsonSerializer.Serialize(await store.ReadMessagesAsync(generation.ConversationId)));
            for (var index = 0; index < cacheKeys.Length; index++)
                Assert.Equal(oldCaches[index], JsonSerializer.Serialize(await store.LoadCompletedSummaryAsync(taskId, cacheKeys[index])));
            history = new(generation.ConversationId, store, coordinator, () => false, () => throw new Exception("read-only history must not pause saves"));
            await history.InitializeAsync();
            var chatView = new ChatView { DataContext = history }; window.Content = chatView; Dispatcher.UIThread.RunJobs();
            Assert.Contains(Texts(chatView), text => text.Contains("较早对话已压缩，原记录保留", StringComparison.Ordinal));
            Assert.Contains(history.Messages, message => message.Body.Length > 0);
            // New acceptance takes the changed configuration; the existing task above stays frozen.
            var conversation = await store.CreateConversationAsync((await store.CreateAsync("synthetic future")).Id);
            var next = await store.AcceptReplyAsync(conversation.Id, "synthetic future question", (await store.LoadActiveConfigurationAsync())!);
            Assert.Equal(SummaryThinkingMode.Disabled, next.Task.Configuration.SummaryThinking);
            Assert.Equal(6, next.ContextPlan!.FormatVersion);
            Assert.Equal(0, vault.Reads); Assert.Equal(0, vault.Writes); Assert.Equal(0, vault.Deletes);
            Assert.Equal(0, transport.StartCount); Assert.Null(transport.DiscoverySecret);
        }
        finally { history?.Detach(); window.Close(); transport.Close(); }
    }

    private static async Task<SettingsViewModel> Open(F f, RecordingVault vault, Func<bool>? blocked = null, Action? persistenceFailed = null)
    {
        var settings = new SettingsViewModel(f.Store, new(f.Store, vault, f.Transport), blocked ?? (() => false), persistenceFailed);
        await settings.InitializeAsync(); return settings;
    }
    private static SettingsView Mount(SettingsViewModel settings, out Window window)
    {
        var view = new SettingsView { DataContext = settings };
        window = new Window { Content = view, Width = 1000, Height = 1000 }; window.Show();
        Dispatcher.UIThread.RunJobs(); return view;
    }
    private static ComboBox Selector(SettingsView view) => Assert.IsType<ComboBox>(view.FindControl<ComboBox>("SummaryThinkingSelector"));
    private static IEnumerable<string> Texts(Control view) => view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "");
    private static TextBox Field(SettingsView view, string name) => Assert.Single(view.GetVisualDescendants().OfType<TextBox>(), field => AutomationProperties.GetName(field) == name);
    private static Button Button(SettingsView view, string text) => Assert.Single(view.GetVisualDescendants().OfType<Button>(), button => button.Content as string == text);
    private static async Task Save(SettingsView view) { await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Button(view, "保存配置").Command!).ExecuteAsync(null); Dispatcher.UIThread.RunJobs(); }
    private static async Task Discover(SettingsView view) { await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)Button(view, "获取模型列表").Command!).ExecuteAsync(null); Dispatcher.UIThread.RunJobs(); }
    private static void AssertNoSecretOrRequest(RecordingVault vault, F f)
    {
        Assert.Equal(0, vault.Reads); Assert.Equal(0, vault.Writes); Assert.Equal(0, vault.Deletes);
        Assert.Equal(0, f.Transport.StartCount); Assert.Null(f.Transport.DiscoverySecret);
    }
    private sealed class RecordingVault : ICredentialVault
    {
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public int Deletes { get; private set; }
        public TaskCompletionSource<string>? PendingWrite { get; init; }
        public Task<string> WriteNewAsync(string secret) { Writes++; return PendingWrite?.Task ?? Task.FromResult("synthetic-new-version"); }
        public Task<string> ReadAsync(string reference) { Reads++; return Task.FromResult("synthetic-only"); }
        public Task DeleteAsync(string reference) { Deletes++; return Task.CompletedTask; }
    }
}
