using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mindweave.Application.Languages;
using Mindweave.Application.Models;
using Mindweave.Application.Tools;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Core.Resources;
using Mindweave.Core.Runs;
using Mindweave.Infrastructure.Resources;
using Mindweave.Infrastructure.Tests.Chat;
using Mindweave.Infrastructure.Tests.Tools;
using Mindweave.Infrastructure.Tools;

namespace Mindweave.Infrastructure.Tests.Languages;

/// <summary>
/// Tools and web text follow the interface language (docs/design/bilingual-ui.md §5, plan Task 16): what the model is told,
/// what is stored, the web fetch's Accept-Language, and messages held for later.
/// </summary>
[Collection(LanguageTests.Name)]
public partial class ToolTextTests
{
    private static readonly ModelSettingsInput ToolModel = new(
        "fake", "https://fake.example/v1", "fake-model", "sk-test",
        Capabilities: ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions) with { ToolCalls = true });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Accept_language_follows_the_interface_language()
    {
        try
        {
            var handler = new FakeHandler(_ => Responses.Text("<html><head><title>t</title></head><body><p>x</p></body></html>"));
            var fetcher = new GuardedPageFetcher(new HttpClient(handler), (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }), [new PdfTextExtractor()]);

            CurrentLanguage.Set(UiLanguage.English);
            await fetcher.FetchAsync(new Uri("https://example.com/a"), Ct);
            CurrentLanguage.Set(UiLanguage.Chinese);
            await fetcher.FetchAsync(new Uri("https://example.com/b"), Ct);

            Assert.Equal(
                ["en-US,en;q=0.9,zh-CN;q=0.8", "zh-CN,zh;q=0.9,en;q=0.8"],
                handler.Requests.Select(r => r.Headers["Accept-Language"]));
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    /// <summary>The built-in tools as the model sees them: English under English, the same arguments in both languages.</summary>
    [Fact]
    public void Tool_definitions_exist_in_both_languages_with_the_same_arguments()
    {
        ITool[] tools = [new WebSearchTool(null!), new FetchPageTool(null!), new SearchAttachmentsTool(null!), new SearchSummariesTool(null!)];
        try
        {
            foreach (var tool in tools)
            {
                CurrentLanguage.Set(UiLanguage.English);
                var english = (tool.Name, tool.DisplayName, tool.Description, tool.ParametersSchema);
                CurrentLanguage.Set(UiLanguage.Chinese);
                var chinese = (tool.Name, tool.DisplayName, tool.Description, tool.ParametersSchema);

                Assert.Equal(english.Name, chinese.Name);
                Assert.DoesNotMatch(Cjk(), english.DisplayName + english.Description + english.ParametersSchema);
                Assert.Matches(Cjk(), chinese.Description);
                Assert.Equal(Shape(chinese.ParametersSchema), Shape(english.ParametersSchema));
                Assert.EndsWith(".", english.Description, StringComparison.Ordinal);
            }
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    /// <summary>
    /// A tool's image placeholder is replaced whichever language it was written in (the language may switch while the tool
    /// runs), and the stored marker is written in the language of the moment; a reader finds the image from either marker.
    /// </summary>
    [Theory]
    [InlineData("[图片 1]", UiLanguage.English, "[Image: resource ")]
    [InlineData("[Image 1]", UiLanguage.Chinese, "[图片：资源 ")]
    [InlineData("[Image 1]", UiLanguage.English, "[Image: resource ")]
    public async Task An_image_placeholder_in_either_language_becomes_the_stored_marker(string placeholder, UiLanguage language, string marker)
    {
        try
        {
            CurrentLanguage.Set(language);
            var tool = new PictureTool(placeholder);
            await using var h = await ChatHarness.StartAsync(model: ToolModel, tools: [tool]);
            h.Client.CallTool(tool.Name, "{}");
            h.Client.Reply("ok");

            var sent = await h.Conversations.StartAsync(h.Workspace, "q", Ct);
            var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);

            var stored = (await h.ResourceService.GetAsync(answer.Blocks.OfType<ToolResultBlock>().Single().Resource!.Value, Ct))!;
            Assert.Contains(marker, stored.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(placeholder, stored.Text, StringComparison.Ordinal);
            var image = ResourceId.Parse(Assert.Single(ImageMarker().Matches(stored.Text!)).Groups[1].Value);
            Assert.Equal("image/png", (await h.ResourceService.GetAsync(image, Ct))!.Resource.MediaType);
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    [Fact]
    public async Task A_search_failure_held_for_later_follows_a_switch()
    {
        try
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
            var provider = new TavilySearchProvider(new HttpClient(new FakeHandler(_ => Responses.Text("{}", "application/json", (HttpStatusCode)429))));

            var failure = await Assert.ThrowsAsync<SearchProviderException>(() => provider.SearchAsync("q", 5, "tvly-key", Ct));

            Assert.Equal("搜索过于频繁，请稍后再试。", failure.Message);
            CurrentLanguage.Set(UiLanguage.English);
            Assert.Equal("Too many searches. Try again later.", failure.Message);
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    /// <summary>A schema's property names and types, whatever language its descriptions are in.</summary>
    private static string Shape(string schema)
    {
        using var document = JsonDocument.Parse(schema);
        var root = document.RootElement;
        var properties = root.GetProperty("properties").EnumerateObject()
            .Select(p => p.Name + ":" + p.Value.GetProperty("type").GetString());
        var required = root.TryGetProperty("required", out var r) ? string.Join(',', r.EnumerateArray().Select(e => e.GetString())) : string.Empty;
        return string.Join(';', properties) + "|" + required;
    }

    /// <summary>The stored image marker in either language, as a reader of stored tool results matches it.</summary>
    [GeneratedRegex(@"\[(?:图片：资源|Image: resource) ([^\]]+)\]")]
    private static partial Regex ImageMarker();

    [GeneratedRegex(@"\p{IsCJKUnifiedIdeographs}|[　-〿＀-￯]")]
    private static partial Regex Cjk();

    /// <summary>A tool that returns one image, standing in its text as <paramref name="placeholder"/>.</summary>
    private sealed class PictureTool(string placeholder) : ITool
    {
        public string Name => "picture_tool";

        public string DisplayName => "picture_tool";

        public string Description => "A tool for tests.";

        public string ParametersSchema => "{\"type\":\"object\"}";

        public ToolRisk Risk => ToolRisk.LocalRead;

        public TimeSpan Timeout => TimeSpan.FromSeconds(20);

        public Task<bool> IsAvailableAsync(ToolScope scope, CancellationToken cancellationToken) => Task.FromResult(true);

        public string Describe(JsonElement arguments) => string.Empty;

        public Task<ToolOutput> ExecuteAsync(ToolScope scope, JsonElement arguments, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolOutput("see " + placeholder, "done", ResourceProvenance.Workspace, [])
            {
                Images = [new ToolImage(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 }, "image/png")],
            });
    }
}
