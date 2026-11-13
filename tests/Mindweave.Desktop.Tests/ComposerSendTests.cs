using Mindweave.Application.Conversations;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.ViewModels.Conversations;

namespace Mindweave.Desktop.Tests;

/// <summary>Low finding ui-vm-10: what is typed while a question is being sent is the next question, not cleared with it.</summary>
public class ComposerSendTests
{
    [Fact]
    public async Task Text_typed_while_a_question_is_sent_stays_in_the_box()
    {
        var gate = new TaskCompletionSource<SendOutcome>();
        var composer = new Composer(_ => gate.Task) { Text = "第一个问题" };

        var sending = composer.SendCommand.ExecuteAsync(null);
        composer.Text = "接着想到的问题";
        gate.SetResult(new SendOutcome(null, ConversationId.New(), MessageId.New()));
        await sending;

        Assert.Equal("接着想到的问题", composer.Text);
    }

    [Fact]
    public async Task The_sent_question_is_cleared_when_nothing_else_was_typed()
    {
        var composer = new Composer(_ => Task.FromResult(new SendOutcome(null, ConversationId.New(), MessageId.New()))) { Text = "问题" };

        await composer.SendCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, composer.Text);
    }
}
