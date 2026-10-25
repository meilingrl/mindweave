using System.Text;
using Mindweave.Core.Chat;

namespace Mindweave.Application.Chat;

internal static class ChatRequestBudget
{
    // Deliberately conservative estimate: one token per UTF-8 byte, 32 per message,
    // 256 fixed safety tokens, and the complete requested output reserve. S1 never truncates.
    public static bool Fits(AiConfiguration configuration, IEnumerable<ChatInput> messages)
    {
        long estimate = 256L + configuration.MaxOutputTokens;
        foreach (var message in messages) estimate += 32L + Encoding.UTF8.GetByteCount(message.Content);
        return estimate <= configuration.ContextTokenBudget;
    }

    public static ChatInput[] Inputs(IEnumerable<ChatMessage> messages) => messages.Select(message =>
        new ChatInput(message.Role == MessageRole.User ? "user" : "assistant", message.Body)).ToArray();
}
