using Mindweave.Application.Models;

namespace Mindweave.Infrastructure.Tests.Chat;

public class ModelSettingsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("https://api.deepseek.com/", "https://api.deepseek.com")]
    [InlineData(" https://relay.example.com/v1 ", "https://relay.example.com/v1")]
    [InlineData("http://localhost:8080/v1/", "http://localhost:8080/v1")]
    public void Base_urls_are_normalized(string raw, string expected) =>
        Assert.Equal(expected, ModelSettingsService.NormalizeBaseUrl(raw));

    [Theory]
    [InlineData("api.deepseek.com", ModelSettingsProblem.InvalidUrl)]
    [InlineData("ftp://x.example", ModelSettingsProblem.InvalidUrl)]
    [InlineData("http://api.example.com/v1", ModelSettingsProblem.InsecureUrl)]
    public void Bad_urls_are_rejected(string raw, ModelSettingsProblem problem) =>
        Assert.Equal(problem, Assert.Throws<ModelSettingsException>(() => ModelSettingsService.NormalizeBaseUrl(raw)).Problem);

    [Fact]
    public async Task The_key_goes_to_the_credential_store_and_only_its_reference_to_the_database()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);

        await h.Models.SaveAsync(new ModelSettingsInput("", "https://api.example.com/v1", "m1", "sk-secret"), Ct);

        var settings = await h.Models.GetAsync(Ct);
        Assert.Equal("api.example.com", settings!.ConnectionName);
        Assert.Equal("sk-secret", h.Credentials.Items[settings.CredentialRef]);
        var dump = await h.Database.ReadAsync(c => string.Join('\n', Rows(c)), Ct);
        Assert.DoesNotContain("sk-secret", dump, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saving_without_a_key_needs_an_existing_one()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);

        var error = await Assert.ThrowsAsync<ModelSettingsException>(() => h.Models.SaveAsync(new ModelSettingsInput("", "https://a.example", "m", null), Ct));

        Assert.Equal(ModelSettingsProblem.MissingKey, error.Problem);
        await h.Models.SaveAsync(new ModelSettingsInput("", "https://a.example", "m", "k1"), Ct);
        await h.Models.SaveAsync(new ModelSettingsInput("", "https://a.example", "m2", null), Ct);
        Assert.Equal("m2", (await h.Models.GetAsync(Ct))!.Model);
        Assert.Single(h.Credentials.Items);
    }

    [Fact]
    public async Task A_saved_key_is_never_sent_to_another_host_by_fetching_testing_or_saving()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);
        await h.Models.SaveAsync(new ModelSettingsInput("", "https://api.deepseek.com", "deepseek-chat", "sk-deepseek"), Ct);
        var saved = (await h.Models.GetAsync(Ct))!;

        // Another vendor's address with the key field left empty: refused before anything is sent.
        var fetch = await Assert.ThrowsAsync<ModelSettingsException>(() => h.Models.DiscoverModelsAsync("https://api.openai.com/v1", null, Ct));
        var test = await Assert.ThrowsAsync<ModelSettingsException>(() => h.Models.TestAsync("https://api.openai.com/v1", "gpt", " ", Ct));
        var save = await Assert.ThrowsAsync<ModelSettingsException>(() => h.Models.SaveAsync(new ModelSettingsInput("OpenAI", "https://api.openai.com/v1", "gpt", null), Ct));
        Assert.All([fetch, test, save], e => Assert.Equal(ModelSettingsProblem.MissingKey, e.Problem));
        Assert.Empty(h.Client.KeysSent);
        Assert.Equal(saved, await h.Models.GetAsync(Ct));

        // Another port on the same machine is another service too.
        await Assert.ThrowsAsync<ModelSettingsException>(() => h.Models.DiscoverModelsAsync("https://api.deepseek.com:8443", null, Ct));
        Assert.Empty(h.Client.KeysSent);

        // The same host (another path, other letter case) is the same service and keeps the saved key.
        await h.Models.DiscoverModelsAsync("https://API.deepseek.com/v1", null, Ct);
        Assert.Equal(("https://API.deepseek.com/v1".ToLowerInvariant(), "sk-deepseek"), Assert.Single(h.Client.KeysSent));

        // With the new vendor's own key, saving works and the old key is retired.
        await h.Models.SaveAsync(new ModelSettingsInput("OpenAI", "https://api.openai.com/v1", "gpt", "sk-openai"), Ct);
        Assert.Equal("sk-openai", Assert.Single(h.Credentials.Items).Value);
    }

    [Fact]
    public async Task A_replaced_key_is_kept_while_an_unfinished_task_uses_it_then_deleted()
    {
        await using var h = await ChatHarness.StartAsync();
        var oldRef = (await h.Models.GetAsync(Ct))!.CredentialRef;
        var held = h.Client.Hold("…");
        var sent = await h.Conversations.StartAsync(h.Workspace, "q", Ct);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        await h.Models.SaveAsync(new ModelSettingsInput("", "https://fake.example/v1", "fake-model", "sk-new"), Ct);

        Assert.True(h.Credentials.Items.ContainsKey(oldRef));
        held.Finish();
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        await ChatHarness.UntilAsync(() => h.Generation.UnfinishedCount == 0);
        await h.Models.CleanUpRetiredCredentialsAsync(Ct);
        Assert.False(h.Credentials.Items.ContainsKey(oldRef));
        Assert.Equal("sk-new", Assert.Single(h.Credentials.Items).Value);
    }

    private static IEnumerable<string> Rows(Microsoft.Data.Sqlite.SqliteConnection c)
    {
        foreach (var table in new[] { "service_connection", "model_config", "app_state" })
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {table};";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                for (var i = 0; i < r.FieldCount; i++)
                {
                    yield return r.GetValue(i)?.ToString() ?? string.Empty;
                }
            }
        }
    }

    /// <summary>Low finding security-5: credentials in the address are refused, not stored in plain text.</summary>
    [Theory]
    [InlineData("https://user:secret@api.example.com/v1")]
    [InlineData("https://token@api.example.com/v1")]
    public void An_address_carrying_credentials_is_refused(string address)
    {
        var problem = Assert.Throws<ModelSettingsException>(() => ModelSettingsService.NormalizeBaseUrl(address));

        Assert.Equal(ModelSettingsProblem.CredentialsInUrl, problem.Problem);
    }
}
