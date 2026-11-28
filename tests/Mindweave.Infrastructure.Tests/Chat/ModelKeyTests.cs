using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application;
using Mindweave.Application.Conversations;
using Mindweave.Application.Credentials;
using Mindweave.Application.Models;
using Mindweave.Application.Startup;
using Mindweave.Core.Conversations;
using Mindweave.Infrastructure.Credentials;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>D2 increment 2 (diagnostics-and-upkeep.md §2.3, §2.6, §2.7): removing the model key, cleaning retired keys, the key store's wait.</summary>
public class ModelKeyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>§2.7 scenario 6.</summary>
    [Fact]
    public async Task Removing_the_key_deletes_the_item_and_the_connection_reads_not_configured()
    {
        await using var h = await ChatHarness.StartAsync();
        var saved = (await h.Models.GetAsync(Ct))!;

        var removal = await h.Models.RemoveKeyAsync(stopRequests: false, Ct);

        Assert.True(removal.Removed);
        Assert.Empty(h.Credentials.Items);
        Assert.Null(await h.Models.GetActiveSnapshotAsync(Ct));
        Assert.False(await h.Models.SavedKeyExistsAsync(Ct));
        var after = (await h.Models.GetAsync(Ct))!;
        Assert.Equal(saved with { CredentialRef = string.Empty }, after);

        // A new question says the model is not set up, and nothing is sent.
        var sent = await h.Conversations.StartAsync(h.Workspace, "还在吗？", Ct);
        Assert.Equal(SendRejection.NotConfigured, sent.Rejection);
        Assert.Empty(h.Client.KeysSent);

        // The address and model stay; saving needs a key again, and with one the connection works.
        var missing = await Assert.ThrowsAsync<ModelSettingsException>(() => h.Models.SaveAsync(new ModelSettingsInput("fake", saved.BaseUrl, saved.Model, null), Ct));
        Assert.Equal(ModelSettingsProblem.MissingKey, missing.Problem);
        await h.Models.SaveAsync(new ModelSettingsInput("fake", saved.BaseUrl, saved.Model, "sk-again"), Ct);
        Assert.NotNull(await h.Models.GetActiveSnapshotAsync(Ct));
        Assert.Equal("sk-again", Assert.Single(h.Credentials.Items).Value);
        Assert.Empty(await h.Settings.RetiredCredentialRefsAsync(Ct));
    }

    [Fact]
    public async Task Removing_a_key_with_nothing_saved_changes_nothing()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);

        Assert.True((await h.Models.RemoveKeyAsync(stopRequests: false, Ct)).Removed);
        Assert.Null(await h.Models.GetAsync(Ct));
        Assert.Equal(0, await h.Models.RequestsUsingKeyAsync(Ct));
    }

    /// <summary>§2.3: when requests use the key, the user is asked first; nothing is stopped without a yes.</summary>
    [Fact]
    public async Task A_key_in_use_is_removed_only_with_a_yes_and_its_requests_stop_keeping_what_arrived()
    {
        await using var h = await ChatHarness.StartAsync();
        var reference = (await h.Models.GetAsync(Ct))!.CredentialRef;
        var held = h.Client.Hold("已经", "到了");
        var sent = await h.Conversations.StartAsync(h.Workspace, "问", Ct);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(1, await h.Models.RequestsUsingKeyAsync(Ct));
        var asked = await h.Models.RemoveKeyAsync(stopRequests: false, Ct);

        Assert.False(asked.Removed);
        Assert.Equal(1, asked.RequestsUsingKey);
        Assert.True(h.Credentials.Items.ContainsKey(reference));
        Assert.Equal(reference, (await h.Models.GetAsync(Ct))!.CredentialRef);
        Assert.True(h.Conversations.HasUnfinishedAnswer(sent.Conversation));

        var removal = await h.Models.RemoveKeyAsync(stopRequests: true, Ct);

        Assert.True(removal.Removed);
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);
        Assert.Equal(MessageState.Stopped, answer.State);
        Assert.Equal("已经到了", answer.Text);
        Assert.Empty(h.Credentials.Items);
        Assert.Equal(string.Empty, (await h.Models.GetAsync(Ct))!.CredentialRef);
        Assert.Equal(0, await h.Models.RequestsUsingKeyAsync(Ct));
    }

    /// <summary>§2.3: a queued answer counts too, and is cancelled by the stop.</summary>
    [Fact]
    public async Task Queued_answers_using_the_key_are_counted_and_cancelled()
    {
        await using var h = await ChatHarness.StartAsync(slots: 1);
        var held = h.Client.Hold("一");
        var first = await h.Conversations.StartAsync(h.Workspace, "一", Ct);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var second = await h.Conversations.StartAsync(h.Workspace, "二", Ct);

        Assert.Equal(2, (await h.Models.RemoveKeyAsync(stopRequests: false, Ct)).RequestsUsingKey);
        Assert.True((await h.Models.RemoveKeyAsync(stopRequests: true, Ct)).Removed);

        Assert.Equal(MessageState.Stopped, (await h.WaitForAnswerAsync(first.Answer!.Value)).State);
        Assert.Equal(MessageState.Cancelled, (await h.WaitForAnswerAsync(second.Answer!.Value)).State);
        Assert.Empty(h.Credentials.Items);
    }

    /// <summary>§2.6: removing the key while the store is locked keeps the key, and the connection is not marked removed.</summary>
    [Fact]
    public async Task A_locked_store_keeps_the_key_and_the_settings()
    {
        await using var h = await ChatHarness.StartAsync();
        var saved = (await h.Models.GetAsync(Ct))!;
        h.Credentials.Unavailable = true;

        await Assert.ThrowsAsync<CredentialUnavailableException>(() => h.Models.RemoveKeyAsync(stopRequests: false, Ct));

        h.Credentials.Unavailable = false;
        Assert.Equal(saved, await h.Models.GetAsync(Ct));
        Assert.True(h.Credentials.Items.ContainsKey(saved.CredentialRef));
        Assert.NotNull(await h.Models.GetActiveSnapshotAsync(Ct));
        Assert.Empty(await h.Settings.RetiredCredentialRefsAsync(Ct));
    }

    /// <summary>
    /// §2.7 scenario 8, for the retired list (increment 2): a key retired but never deleted — a task held it when the app
    /// ended, or the store was locked — is deleted at the next start; the active key stays.
    /// </summary>
    [Fact]
    public async Task A_retired_key_left_in_the_store_is_deleted_at_start_up()
    {
        using var dir = new TempDataDirectory();
        string active;
        await using (var h = await ChatHarness.StartAsync(directory: dir))
        {
            active = (await h.Models.GetAsync(Ct))!.CredentialRef;
            await h.Settings.SetRetiredCredentialRefsAsync(["c/left-behind"], Ct);
        }

        await using var database = dir.Database();
        var store = new SqliteConversationStore(database);
        var credentials = new MemoryCredentialStore();
        credentials.Items["c/left-behind"] = "sk-old";
        credentials.Items[active] = "sk-test";
        using var retired = new RetiredCredentials(new SqliteModelSettingsStore(database), store, credentials, NullLogger<RetiredCredentials>.Instance);
        var startup = new StartupCoordinator(
            database, new SqliteWorkspaceStore(database), store, new SystemClock(), new SqliteLocationStore(database, new SystemClock()),
            NullLogger<StartupCoordinator>.Instance, new NoResources(), retired);

        Assert.IsType<StartupOutcome.ReadyToWork>(await startup.StartAsync(Ct));
        await retired.Latest;

        Assert.Equal(active, Assert.Single(credentials.Items).Key);
        Assert.Empty(await new SqliteModelSettingsStore(database).RetiredCredentialRefsAsync(Ct));
    }

    /// <summary>
    /// Cleanups in the background and saves never lose a reference: each change to the list goes through one gate. Without
    /// it a cleanup that read the list before a save wrote it back without the save's reference, and that key stayed for good.
    /// </summary>
    [Fact]
    public async Task Saves_racing_background_cleanups_leave_only_the_last_key()
    {
        await using var h = await ChatHarness.StartAsync();

        for (var i = 0; i < 20; i++)
        {
            h.RetiredKeys.CleanUpSoon();
            await h.Models.SaveAsync(new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", $"sk-{i}"), Ct);
        }

        await h.RetiredKeys.Latest;
        await h.RetiredKeys.CleanUpAsync(Ct);
        Assert.Equal("sk-19", Assert.Single(h.Credentials.Items).Value);
        Assert.Empty(await h.Settings.RetiredCredentialRefsAsync(Ct));
    }

    /// <summary>
    /// §2.3, gap-native-interop-1: a key store that does not answer fails the request after the wait, with the reason, and
    /// the request gives its slot up: the next one runs.
    /// </summary>
    [Fact]
    public async Task A_key_store_that_does_not_answer_fails_the_request_and_frees_its_slot()
    {
        await using var h = await ChatHarness.StartAsync(slots: 1);
        h.Credentials.Hangs = true;

        var stuck = await h.Conversations.StartAsync(h.Workspace, "一", Ct);
        var failed = await h.WaitForAnswerAsync(stuck.Answer!.Value, timeoutMs: 10_000);

        Assert.Equal(MessageState.Failed, failed.State);
        Assert.Equal(GenerationErrorKind.CredentialUnavailable, failed.ErrorKind);
        Assert.Contains("did not answer", failed.ErrorDetail, StringComparison.Ordinal);

        h.Credentials.Hangs = false;
        h.Client.Reply("好");
        var next = await h.Conversations.StartAsync(h.Workspace, "二", Ct);
        Assert.Equal(MessageState.Complete, (await h.WaitForAnswerAsync(next.Answer!.Value)).State);
    }

    [Fact]
    public async Task Every_key_store_call_is_limited_to_the_wait()
    {
        var time = new ManualTime();
        var store = CredentialStores.Limited(new SilentStore(), CredentialStores.AnswerWait, time);

        var read = store.ReadAsync("r", Ct);
        var write = store.WriteAsync("r", "s", Ct);
        var delete = store.DeleteAsync("r", Ct);
        time.Advance(CredentialStores.AnswerWait - TimeSpan.FromSeconds(1));
        Assert.False(read.IsCompleted || write.IsCompleted || delete.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(1));

        foreach (var call in new Task[] { read, write, delete })
        {
            var error = await Assert.ThrowsAsync<CredentialUnavailableException>(() => call);
            Assert.Contains("did not answer within 30 seconds", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_key_store_that_answers_in_time_is_passed_through()
    {
        var inner = new MemoryCredentialStore();
        var store = CredentialStores.Limited(inner, CredentialStores.AnswerWait, new ManualTime());

        await store.WriteAsync("r", "s", Ct);
        Assert.Equal("s", await store.ReadAsync("r", Ct));
        await store.DeleteAsync("r", Ct);
        Assert.Null(await store.ReadAsync("r", Ct));

        inner.Unavailable = true;
        await Assert.ThrowsAsync<CredentialUnavailableException>(() => store.ReadAsync("r", Ct));
    }

    private sealed class SilentStore : ICredentialStore
    {
        public Task<string?> ReadAsync(string reference, CancellationToken cancellationToken) => new TaskCompletionSource<string?>().Task;

        public Task WriteAsync(string reference, string secret, CancellationToken cancellationToken) => new TaskCompletionSource().Task;

        public Task DeleteAsync(string reference, CancellationToken cancellationToken) => new TaskCompletionSource().Task;
    }
}
