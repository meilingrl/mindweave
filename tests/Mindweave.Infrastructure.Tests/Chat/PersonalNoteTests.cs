using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Application.Languages;
using Mindweave.Application.Preferences;
using Mindweave.Application.Prompts;
using Mindweave.Core.Context;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// The personal note (requirements §10.9, docs/design/reply-quality.md Q4, Q9): one text from Settings that goes with every
/// answer request and every summary request, marked as the user's own preferences, and stands at the start with the
/// instructions that are the same for every conversation (architecture §15.5).
/// </summary>
[Collection(Languages.LanguageTests.Name)]
public class PersonalNoteTests
{
    private const string Note = "我是中学生物老师，物理基础弱。回答先给结论。";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_note_goes_with_the_next_answer_and_summary_and_clearing_it_removes_it()
    {
        await using var h = await ChatHarness.StartAsync();
        var note = new PersonalNoteService(h.Preferences);
        var service = h.PersonalNote;
        await service.SetAsync(Note, Ct);

        // Acceptance 7: in the next answer request, after the answer instructions in the turn that opens it.
        var (root, _) = await h.RootWithAnswerAsync("火车是怎么发明的？");
        var answer = h.Client.Requests.Last(FakeChatClient.IsAnswer);
        Assert.Equal(new ChatTurn(ChatRole.System, AnswerPrompt.Opening(Note)), answer.Turns[0]);
        Assert.StartsWith(AnswerPrompt.Fixed + "\n\n", answer.Turns[0].Content, StringComparison.Ordinal);
        Assert.EndsWith("<user-note>\n" + Note + "\n</user-note>", answer.Turns[0].Content, StringComparison.Ordinal);
        Assert.Single(answer.Turns, t => t.Content.Contains(Note, StringComparison.Ordinal));

        // And in the next summary request.
        await h.SettleAsync(root);
        await h.SummarizeAsync(root);
        var summary = h.Client.Requests.Last(FakeChatClient.IsKnowledgeSummary);
        Assert.Contains(AnswerPrompt.NotePart(Note), summary.Turns[0].Content, StringComparison.Ordinal);
        Assert.StartsWith(PromptLibrary.Get("summary"), summary.Turns[0].Content, StringComparison.Ordinal);

        // Stored: another service over the same preferences reads it.
        Assert.Equal(Note, await note.GetAsync(Ct));

        // Cleared: in neither.
        await service.SetAsync("   ", Ct);
        Assert.Equal(string.Empty, await new PersonalNoteService(h.Preferences).GetAsync(Ct));
        await h.AskAsync(root, "后来呢？");
        var after = h.Client.Requests.Last(FakeChatClient.IsAnswer);
        Assert.Equal(new ChatTurn(ChatRole.System, AnswerPrompt.Fixed), after.Turns[0]);
        Assert.DoesNotContain(after.Turns, t => t.Content.Contains(Note, StringComparison.Ordinal) || t.Content.Contains("<user-note>", StringComparison.Ordinal));
        h.Client.Knowledge.Replies.Enqueue("标题：蒸汽机车\n短名：机车\n\n正文");
        await h.SummarizeAsync(root);
        Assert.DoesNotContain("<user-note>", h.Client.Requests.Last(FakeChatClient.IsKnowledgeSummary).Turns[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_root_and_its_branch_begin_with_the_same_turn_with_the_note()
    {
        await using var h = await ChatHarness.StartAsync();
        await h.PersonalNote.SetAsync(Note, Ct);
        var (root, answer) = await h.RootWithAnswerAsync("火车是怎么发明的？");
        var child = await h.BranchAsync(root, answer, "火车");
        await h.AskAsync(child, "谁造了第一台？");
        var requests = h.Client.Requests.Where(FakeChatClient.IsAnswer).ToList();
        Assert.Equal(2, requests.Count);
        Assert.Equal(requests[0].Turns[0], requests[1].Turns[0]);
        Assert.Equal(AnswerPrompt.BranchPart(true), FakeChatClient.NoteOf(requests[1]));
    }

    [Fact]
    public async Task The_note_counts_in_the_context_budget()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, _) = await h.RootWithAnswerAsync("火车是怎么发明的？");
        var before = (await h.Conversations.GetContextUsageAsync(root, Ct))!;
        await h.PersonalNote.SetAsync(new string('注', 400), Ct);
        var after = (await h.Conversations.GetContextUsageAsync(root, Ct))!;
        Assert.True(after.FullTokens - before.FullTokens >= 400, $"{before.FullTokens} → {after.FullTokens}");
    }

    [Fact]
    public async Task More_than_the_limit_is_refused_and_nothing_is_cut()
    {
        await using var h = await ChatHarness.StartAsync();
        var note = new PersonalNoteService(h.Preferences);
        await note.SetAsync(new string('a', PersonalNoteService.MaxLength), Ct);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => note.SetAsync(new string('b', PersonalNoteService.MaxLength + 1), Ct));
        Assert.Equal(new string('a', PersonalNoteService.MaxLength), await new PersonalNoteService(h.Preferences).GetAsync(Ct));
    }

    [Fact]
    public void The_note_part_marks_the_note_as_the_users_and_is_empty_without_one()
    {
        Assert.Equal(string.Empty, AnswerPrompt.NotePart(null));
        Assert.Equal(string.Empty, AnswerPrompt.NotePart(" \n "));
        Assert.Equal(AnswerPrompt.Fixed, AnswerPrompt.Opening(string.Empty));

        // A note with braces is taken as written.
        Assert.Contains("{x} 与 {note}", AnswerPrompt.NotePart("{x} 与 {note}"), StringComparison.Ordinal);
        foreach (var language in new[] { UiLanguage.English, UiLanguage.Chinese })
        {
            Assert.Contains("{note}", PromptLibrary.Get("answer-note", language), StringComparison.Ordinal);
            Assert.Contains("<user-note>", PromptLibrary.Get("answer-note", language), StringComparison.Ordinal);
        }

        // The note part with a full English note stays near what the design counted (Q4: about 200 tokens). The app's
        // estimate counts high: 242 when written (2026-11-28). A Chinese note counts a token a character.
        try
        {
            CurrentLanguage.Set(UiLanguage.English);
            var english = TokenEstimator.Estimate(AnswerPrompt.NotePart(string.Concat(Enumerable.Repeat("I teach biology. ", 30))[..PersonalNoteService.MaxLength]));
            Assert.True(english <= 260, $"English note part: {english}");
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }
}
