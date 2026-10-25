namespace Mindweave.Core.Chat;

public static class ChatHistory
{
    /// <summary>Users in conversation order and only the newest answer attempt for each user.</summary>
    public static IEnumerable<ChatMessage> LatestInput(IReadOnlyList<ChatMessage> messages)
    {
        foreach (var user in messages.Where(message => message.Role == MessageRole.User))
        {
            yield return user;
            var answer = messages.LastOrDefault(message => message.ReplyToUserId == user.Id);
            if (answer is not null) yield return answer;
        }
    }
}
