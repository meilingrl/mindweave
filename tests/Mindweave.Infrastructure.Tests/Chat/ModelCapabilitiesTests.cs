using Mindweave.Application.Models;
using Mindweave.Core.Models;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Infrastructure.Tests.Chat;

public class ModelCapabilitiesTests
{
    private static readonly ModelCapabilities Declared = new(Streaming: true, ToolCalls: true, ImageInput: false, ReasoningOutput: true, PromptCaching: false, ContextWindowTokens: 128_000);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Declared_capabilities_survive_a_save_and_a_re_save_of_the_same_model()
    {
        await using var h = await ChatHarness.StartAsync();
        var settings = (await h.Settings.GetActiveAsync(Ct))!;
        Assert.Equal(ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions), settings.Capabilities);

        await h.Settings.SaveActiveAsync(settings with { Capabilities = Declared }, DateTimeOffset.UtcNow, Ct);

        // The settings form shows and saves the context capacity; every other declared capability is kept.
        await h.Models.SaveAsync(new ModelSettingsInput("fake", "https://fake.example/v1", settings.Model, null, Declared.ContextWindowTokens), Ct);
        Assert.Equal(Declared, (await h.Settings.GetActiveAsync(Ct))!.Capabilities);

        await h.Models.SaveAsync(new ModelSettingsInput("fake", "https://fake.example/v1", settings.Model, null), Ct);
        Assert.Equal(Declared with { ContextWindowTokens = null }, (await h.Settings.GetActiveAsync(Ct))!.Capabilities);
    }

    [Fact]
    public async Task Switching_model_starts_from_the_protocol_defaults()
    {
        await using var h = await ChatHarness.StartAsync();
        var settings = (await h.Settings.GetActiveAsync(Ct))!;
        await h.Settings.SaveActiveAsync(settings with { Capabilities = Declared }, DateTimeOffset.UtcNow, Ct);

        await h.Models.SaveAsync(new ModelSettingsInput("fake", "https://fake.example/v1", "another-model", null), Ct);

        Assert.Equal(ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions), (await h.Settings.GetActiveAsync(Ct))!.Capabilities);
    }

    [Fact]
    public async Task Tasks_freeze_the_capabilities_they_were_accepted_with()
    {
        await using var h = await ChatHarness.StartAsync();
        var settings = (await h.Settings.GetActiveAsync(Ct))!;
        await h.Settings.SaveActiveAsync(settings with { Capabilities = Declared }, DateTimeOffset.UtcNow, Ct);

        var sent = await h.Conversations.StartAsync(h.Workspace, "q", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        await h.Settings.SaveActiveAsync(settings, DateTimeOffset.UtcNow, Ct);

        var stored = await h.Database.ReadAsync(
            c =>
            {
                using var cmd = c.Command(null, "SELECT capabilities FROM generation_task WHERE target_message_id = $id;", ("$id", sent.Answer!.Value.ToString()));
                return cmd.ExecuteScalar() as string;
            },
            Ct);
        Assert.Equal(Declared, SqlNames.ParseCapabilities(stored, ChatProtocol.ChatCompletions));
    }

    [Fact]
    public async Task Capabilities_the_form_declares_are_saved_with_the_context_capacity_the_form_says()
    {
        await using var h = await ChatHarness.StartAsync();
        var settings = (await h.Settings.GetActiveAsync(Ct))!;
        await h.Settings.SaveActiveAsync(settings with { Capabilities = Declared }, DateTimeOffset.UtcNow, Ct);

        // Turning image input on and tool calls off for the same model.
        var form = Declared with { ToolCalls = false, ImageInput = true, ContextWindowTokens = 999 };
        await h.Models.SaveAsync(new ModelSettingsInput("fake", "https://fake.example/v1", settings.Model, null, 64_000, form), Ct);
        Assert.Equal(form with { ContextWindowTokens = 64_000 }, (await h.Settings.GetActiveAsync(Ct))!.Capabilities);

        // A different model takes what the form declares, not the protocol defaults.
        await h.Models.SaveAsync(new ModelSettingsInput("fake", "https://fake.example/v1", "vision-model", null, null, form), Ct);
        Assert.Equal(form with { ContextWindowTokens = null }, (await h.Settings.GetActiveAsync(Ct))!.Capabilities);
    }

    [Fact]
    public async Task Features_ask_the_active_model_and_unknown_means_no()
    {
        await using (var none = await ChatHarness.StartAsync(configure: false))
        {
            foreach (var capability in Enum.GetValues<ModelCapability>())
            {
                Assert.False(await none.Models.ActiveModelHasAsync(capability, Ct));
            }
        }

        await using var h = await ChatHarness.StartAsync();
        Assert.False(await h.Models.ActiveModelHasAsync(ModelCapability.ToolCalls, Ct));
        Assert.False(await h.Models.ActiveModelHasAsync(ModelCapability.ImageInput, Ct));
        Assert.True(await h.Models.ActiveModelHasAsync(ModelCapability.Streaming, Ct));

        var settings = (await h.Settings.GetActiveAsync(Ct))!;
        await h.Models.SaveAsync(new ModelSettingsInput("fake", "https://fake.example/v1", settings.Model, null, null, settings.Capabilities with { ImageInput = true }), Ct);
        Assert.True(await h.Models.ActiveModelHasAsync(ModelCapability.ImageInput, Ct));
        Assert.False(await h.Models.ActiveModelHasAsync(ModelCapability.ToolCalls, Ct));
    }
}
