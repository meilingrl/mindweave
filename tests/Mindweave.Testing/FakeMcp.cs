using Mindweave.Application.Tools.Mcp;

namespace Mindweave.Testing;

/// <summary>Drafts that run the fake MCP server (tests/Mindweave.FakeMcpServer) from the test's output folder.</summary>
public static class FakeMcp
{
    /// <summary>The dotnet host running the tests, so the server starts without relying on PATH.</summary>
    public static string Dotnet => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";

    public static string ServerDll => Path.Combine(AppContext.BaseDirectory, "Mindweave.FakeMcpServer.dll");

    public static McpServerDraft Command(string name = "测试服务器", IReadOnlyDictionary<string, string>? environment = null, params string[] flags) =>
        new(name, McpServerKind.Command, Dotnet, [ServerDll, .. flags], environment);
}
