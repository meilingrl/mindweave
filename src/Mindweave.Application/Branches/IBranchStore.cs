using Mindweave.Core.Branches;
using Mindweave.Core.Chat;

namespace Mindweave.Application.Branches;

public interface IBranchStore
{
    Task<Conversation> CreateBranchAsync(Guid parentConversationId, SourceSelection selection, Guid operationId, ConversationReadState? parentReadState = null);
    Task<BranchSource?> LoadBranchSourceAsync(Guid childConversationId);
    Task<IReadOnlyList<BranchSource>> ListChildBranchesAsync(Guid parentConversationId);
    Task<IReadOnlyList<FrozenContextRange>> ReadFrozenContextRangesAsync(Guid conversationId);
    Task<ConversationReadState?> LoadReadStateAsync(Guid conversationId);
    Task SaveReadStateAsync(ConversationReadState state);
}
