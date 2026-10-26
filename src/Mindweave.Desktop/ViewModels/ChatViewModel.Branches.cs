using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Branches;
using Mindweave.Application.Navigation;
using Mindweave.Core.Branches;

namespace Mindweave.Desktop.ViewModels;

public sealed record BranchBookmark(Guid ChildConversationId, string Title, string Quote);

public sealed partial class ChatViewModel
{
    private IBranchStore? branchStore;
    private Action<ChatViewModel, ConversationReadState>? readingChanged;
    private Func<bool> isCurrent = () => false;
    private Func<bool> navigationAvailable = () => true;
    private Func<SourceSelection, Task>? createBranch;
    public ConversationReadState ReadState { get; private set; } = new(Guid.Empty, null, 0);
    public SourceNavigation? PendingSourceReveal { get; private set; }
    public string BranchPath { get; private set; } = "";
    public string? SavedSourceQuote { get; private set; }
    public bool HasSource { get; private set; }
    public bool IsCurrentPage => isCurrent();
    public bool CanBranch => CanEdit;
    public bool CanNavigate => IsCurrentPage && !IsAccepting && navigationAvailable();
    public IAsyncRelayCommand? ReturnToSourceCommand { get; private set; }
    public IAsyncRelayCommand<BranchBookmark>? OpenBranchCommand { get; private set; }
    public event Action? CaptureReadingRequested;
    public event Action? RevealRequested;

    internal void ConfigureBranches(IBranchStore branches, Func<bool> current, Func<bool> navigationReady,
        Func<SourceSelection, Task> create, Func<Guid, Task> returnToSource, Func<Guid, Task> open,
        Action<ChatViewModel, ConversationReadState> changed)
    {
        branchStore = branches; isCurrent = current; navigationAvailable = navigationReady; createBranch = create; readingChanged = changed;
        ReturnToSourceCommand = new AsyncRelayCommand(() => returnToSource(ConversationId));
        OpenBranchCommand = new AsyncRelayCommand<BranchBookmark>(bookmark => bookmark is null ? Task.CompletedTask : open(bookmark.ChildConversationId));
    }
    internal async Task InitializeBranchesAsync(ConversationReadState? pending)
    {
        if (branchStore is null) return;
        ReadState = pending ?? await branchStore.LoadReadStateAsync(ConversationId) ?? new(ConversationId, null, 0);
        var source = await branchStore.LoadBranchSourceAsync(ConversationId); HasSource = source is not null;
        SavedSourceQuote = source?.Quote;
        var titles = new List<string> { TitleText }; var seen = new HashSet<Guid> { ConversationId };
        while (source is not null)
        {
            if (!seen.Add(source.ParentConversationId)) throw new InvalidDataException("来源关系包含循环。");
            var parent = await store.LoadConversationAsync(source.ParentConversationId) ?? throw new InvalidDataException("来源会话不存在。");
            titles.Add(parent.Title); source = await branchStore.LoadBranchSourceAsync(parent.Id);
        }
        titles.Reverse(); BranchPath = string.Join(" › ", titles);
        foreach (var child in await branchStore.ListChildBranchesAsync(ConversationId))
        {
            var conversation = await store.LoadConversationAsync(child.ChildConversationId);
            Messages.FirstOrDefault(m => m.Id == child.MessageId)?.Branches.Add(new(child.ChildConversationId, conversation?.Title ?? "分支", child.Quote));
        }
    }
    internal void RefreshBranchPath(IEnumerable<string> titles)
    { BranchPath = string.Join(" › ", titles); OnPropertyChanged(nameof(BranchPath)); }
    public Task CreateBranchAsync(SourceSelection selection) => IsCurrentPage && CanBranch && createBranch is not null ? createBranch(selection) : Task.CompletedTask;
    public void ReportSelectionFailure(string message) => ErrorMessage = message;
    public void RecordReading(ReadingLocation? location)
    {
        if (!IsCurrentPage || (ReadState.Revision > 0 && ReadState.Location == location)) return;
        if (location is not null && !Messages.Any(m => m.Id == location.MessageId)) return;
        ReadState = new(ConversationId, location, checked(ReadState.Revision + 1));
        readingChanged?.Invoke(this, ReadState);
    }
    internal void CaptureReadingNow() => CaptureReadingRequested?.Invoke();
    internal void RequestSourceReveal(SourceNavigation source)
    { PendingSourceReveal = source; ErrorMessage = null; RevealRequested?.Invoke(); }
    public void ConsumeSourceReveal() => PendingSourceReveal = null;
}
