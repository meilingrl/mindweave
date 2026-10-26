using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application;
using Mindweave.Application.Reading;
using Mindweave.Core.Conversations;
using Mindweave.Core.Reading;

namespace Mindweave.Infrastructure.Tests.Chat;

public class ReadingStateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ViewerSlotKey Slot => ViewerSlotKey.Primary;

    [Fact]
    public async Task The_newest_value_is_read_from_memory_before_it_is_stored()
    {
        await using var h = await ChatHarness.StartAsync();
        var (conversation, answer) = await ConversationAsync(h);
        using var slow = new ReadingStateService(h.ReadingStore, new SystemClock(), NullLogger<ReadingStateService>.Instance, TimeSpan.FromHours(1));
        var position = new ReadingPosition(answer, 0, "{\"c\":5}", 1);

        slow.SetPosition(conversation, Slot, position);
        slow.SetDraft(conversation, Slot, "还没发的问题");

        Assert.Equal(position, await slow.GetPositionAsync(conversation, Slot, Ct));
        Assert.Equal("还没发的问题", await slow.GetDraftAsync(conversation, Slot, Ct));
        Assert.Null(await h.ReadingStore.GetPositionAsync(conversation, Slot, Ct));

        await slow.FlushAsync();
        Assert.Equal(position, await h.ReadingStore.GetPositionAsync(conversation, Slot, Ct));
        Assert.Equal("还没发的问题", await h.ReadingStore.GetDraftAsync(conversation, Slot, Ct));
    }

    [Fact]
    public async Task Changes_are_saved_shortly_after_and_survive_a_restart()
    {
        using var dir = new TempDataDirectory();
        ConversationId conversation;
        MessageId answer;
        await using (var h = await ChatHarness.StartAsync(directory: dir))
        {
            (conversation, answer) = await ConversationAsync(h);
            h.Reading.SetPosition(conversation, Slot, new ReadingPosition(answer, 0, null, 1));
            h.Reading.SetDraft(conversation, Slot, "草稿");
            await ChatHarness.UntilAsync(() => h.ReadingStore.GetDraftAsync(conversation, Slot, Ct).Result == "草稿");
        }

        await using var reopened = await ChatHarness.StartAsync(directory: dir);
        Assert.Equal(answer, (await reopened.Reading.GetPositionAsync(conversation, Slot, Ct))!.Message);
        Assert.Equal("草稿", await reopened.Reading.GetDraftAsync(conversation, Slot, Ct));
    }

    [Fact]
    public async Task An_empty_draft_removes_the_row_and_slots_are_separate()
    {
        await using var h = await ChatHarness.StartAsync();
        var (conversation, _) = await ConversationAsync(h);
        var other = new ViewerSlotKey("second");

        h.Reading.SetDraft(conversation, Slot, "甲");
        h.Reading.SetDraft(conversation, other, "乙");
        await h.Reading.FlushAsync();
        h.Reading.SetDraft(conversation, Slot, string.Empty);
        await h.Reading.FlushAsync();

        Assert.Null(await h.ReadingStore.GetDraftAsync(conversation, Slot, Ct));
        Assert.Equal("乙", await h.ReadingStore.GetDraftAsync(conversation, other, Ct));
    }

    [Fact]
    public async Task A_position_in_another_conversations_message_is_not_stored()
    {
        await using var h = await ChatHarness.StartAsync();
        var (conversation, _) = await ConversationAsync(h);
        var (_, foreign) = await ConversationAsync(h);

        await h.ReadingStore.SavePositionAsync(conversation, Slot, new ReadingPosition(foreign, 0, null, 1), DateTimeOffset.UtcNow, Ct);

        Assert.Null(await h.ReadingStore.GetPositionAsync(conversation, Slot, Ct));
    }

    [Fact]
    public async Task An_oversized_locator_falls_back_to_the_message()
    {
        await using var h = await ChatHarness.StartAsync();
        var (conversation, answer) = await ConversationAsync(h);

        h.Reading.SetPosition(conversation, Slot, new ReadingPosition(answer, 0, new string('x', ReadingPosition.MaxLocatorLength + 1), 1));
        await h.Reading.FlushAsync();

        var stored = await h.ReadingStore.GetPositionAsync(conversation, Slot, Ct);
        Assert.Equal(answer, stored!.Message);
        Assert.Null(stored.Locator);
    }

    private static async Task<(ConversationId, MessageId)> ConversationAsync(ChatHarness h)
    {
        var sent = await h.Conversations.StartAsync(h.Workspace, "问", Ct);
        return (sent.Conversation, (await h.WaitForAnswerAsync(sent.Answer!.Value)).Id);
    }
}
