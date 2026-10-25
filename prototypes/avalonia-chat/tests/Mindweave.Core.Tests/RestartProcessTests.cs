using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Mindweave.Core.Tests;

public class RestartProcessTests
{
    [Fact]
    public async Task A_fresh_reader_process_restores_the_writer_process_snapshot()
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Path, "state.json");
        var writer = await Run("write", path);
        var reader = await Run("read", path);
        Assert.NotEqual(writer.GetProperty("processId").GetInt32(), reader.GetProperty("processId").GetInt32());
        Assert.Equal(writer.GetProperty("currentId").GetString(), reader.GetProperty("currentId").GetString());
        Assert.Equal("蒸汽机", reader.GetProperty("quote").GetString());
        Assert.Equal("rail-history", reader.GetProperty("parentId").GetString());
        Assert.Equal("process draft", reader.GetProperty("draft").GetString());
        Assert.Equal(4, reader.GetProperty("character").GetInt32());
        Assert.Equal("收到的分块内容", reader.GetProperty("partial").GetString());
        Assert.Equal("Interrupted", reader.GetProperty("status").GetString());
        Assert.Equal(5, reader.GetProperty("conversationCount").GetInt32());
    }

    private static async Task<JsonElement> Run(string mode, string path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Mindweave.Prototype.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var probe = Path.Combine(directory!.FullName, "tools", "Mindweave.RestartProbe", "bin", configuration, "net8.0", "Mindweave.RestartProbe.dll");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(probe); start.ArgumentList.Add(mode); start.ArgumentList.Add(path);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(process.ExitCode == 0, await error);
        using var document = JsonDocument.Parse(await output);
        return document.RootElement.Clone();
    }
}
