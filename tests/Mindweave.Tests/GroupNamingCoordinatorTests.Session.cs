using Mindweave.Application.Summaries;
using Mindweave.Core.Chat;
using Mindweave.Desktop.Services;
using Xunit;
namespace Mindweave.Tests;

public sealed partial class GroupNamingCoordinatorTests
{
    [Fact]
    public async Task Session_recovers_without_outbound_and_only_generated_current_event_starts_naming()
    {
        await using var data = await SummaryStorageFixture.Open(); await SummaryNamingStorageTests.Add(data);
        var naming = SummaryNamingStorageTests.Naming(data); var old = await SummaryNamingStorageTests.Accept(data);
        var transport = new GenerationCoordinatorTests.ControlledTransport();
        await data.Store.SaveConfigurationAsync(BranchStorageTests.Config(32768) with { CredentialReference = "v1" });
        await using var session = new DesktopChatSession(data.Store, transport, new GenerationCoordinatorTests.MemoryVault());
        try
        {
            await session.InitializeAsync(); Assert.Equal(GenerationStatus.Interrupted, (await naming.LoadNodeNamingJobAsync(old.Id))!.Status); Assert.Equal(0, transport.StartCount);
            var candidate = (await session.Summaries.AcceptAsync(data.Conversation)).Value!; var summary = await transport.NextAsync(); summary.Emit("{\"Title\":\"First\",\"Markdown\":\"candidate\"}", "stop"); summary.Finish();
            await KnowledgeSummaryCoordinatorTests.Fixture.WaitFor(() => session.Summaries.Snapshots.Single(s => s.TaskId == candidate.Id).Status == GenerationStatus.Completed, h => session.Summaries.Changed += h, h => session.Summaries.Changed -= h);
            Assert.Equal(1, transport.StartCount);
            var conversation = (await data.Store.CreateConversationAsync(data.Workspace)).Id; await BranchStorageTests.Answer(data.Store, conversation, "new question", "new answer");
            await session.Summaries.AcceptAsync(conversation); summary = await transport.NextAsync(); summary.Emit("{\"Title\":\"First\",\"Markdown\":\"new current\"}", "stop"); summary.Finish();
            var rename = await transport.NextAsync(); Assert.Contains("new current", rename.Request.Messages[1].Content); rename.Fail("SyntheticFailure");
        }
        finally { transport.Close(); }
    }
}
