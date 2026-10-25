using System.Text.Json;
using System.Text.Json.Nodes;
using Mindweave.Core;
using Mindweave.Desktop;
using Xunit;

namespace Mindweave.Core.Tests;

public class PersistenceTests
{
    [Fact]
    public async Task Startup_with_a_bad_file_uses_temporary_state_and_never_saves_over_it()
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Path, "state.json");
        await File.WriteAllTextAsync(path, "{unreadable original");
        var workspace = await WorkspaceViewModel.OpenSavedWorkspaceAsync(path);
        Assert.NotNull(workspace.PersistenceError);
        workspace.Current.Draft = "temporary draft";
        workspace.MarkDirty();
        await workspace.SaveAsync();
        Assert.Equal("{unreadable original", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("null-conversation")]
    [InlineData("null-message")]
    [InlineData("wrong-source")]
    public async Task Structurally_invalid_snapshot_is_rejected_without_changing_the_file(string corruption)
    {
        using var folder = new TestFolder();
        var store = new JsonWorkspaceStore(Path.Combine(folder.Path, "state.json"));
        await store.SaveAsync(DemoContent.Create());
        var json = JsonNode.Parse(await File.ReadAllTextAsync(store.FilePath))!;
        if (corruption == "null-conversation") json["conversations"]![0] = null;
        if (corruption == "null-message") json["conversations"]![0]!["messages"]![0] = null;
        if (corruption == "wrong-source") json["conversations"]![1]!["source"]!["quote"] = "unrelated text";
        var original = json.ToJsonString();
        await File.WriteAllTextAsync(store.FilePath, original);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        Assert.Equal(original, await File.ReadAllTextAsync(store.FilePath));
    }

    [Fact]
    public async Task Save_and_load_preserve_current_chat_sources_drafts_and_character_anchor()
    {
        using var folder = new TestFolder();
        var state = DemoContent.Create();
        state.SwitchTo("steam-engine", new("rail-answer/b3", -12, 48), "root draft");
        state.Current.Draft = "child draft";
        state.Current.Position = new("steam-answer/b4", 8, 13);
        var store = new JsonWorkspaceStore(Path.Combine(folder.Path, "state.json"));
        await store.SaveAsync(state);
        var restored = (await store.LoadAsync())!;
        Assert.Equal("steam-engine", restored.Current.Id);
        Assert.Equal("child draft", restored.Current.Draft);
        Assert.Equal(new ReadingPosition("steam-answer/b4", 8, 13), restored.Current.Position);
        Assert.Equal("root draft", restored.Conversations[0].Draft);
        Assert.Equal("蒸汽机", restored.Current.Source!.Quote);
        Assert.Equal("steam-answer", restored.Current.Messages[1].Id);
        Assert.Single(Directory.GetFiles(folder.Path));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(store.FilePath));
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Loading_inflight_output_marks_interrupted_and_keeps_partial_text()
    {
        using var folder = new TestFolder();
        var state = DemoContent.Create();
        state.Current.Messages.Add(new("running", false, "partial answer", ReplyStatus.Generating));
        var store = new JsonWorkspaceStore(Path.Combine(folder.Path, "state.json"));
        await store.SaveAsync(state);
        var loaded = (await store.LoadAsync())!;
        Assert.Equal("partial answer", loaded.Current.Messages.Last().Markdown);
        Assert.Equal(ReplyStatus.Interrupted, loaded.Current.Messages.Last().Status);
    }

    [Fact]
    public async Task Unreadable_json_is_reported_and_the_original_is_not_modified()
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Path, "state.json");
        await File.WriteAllTextAsync(path, "{broken original");
        var store = new JsonWorkspaceStore(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        Assert.Equal("{broken original", await File.ReadAllTextAsync(path));
    }
}

public sealed class TestFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mindweave-prototype-tests-" + Guid.NewGuid().ToString("N"));
    public TestFolder() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, true);
}
