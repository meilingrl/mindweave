using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Core.Chat;
namespace Mindweave.Desktop.ViewModels;
public sealed partial class ConversationItemViewModel(Conversation conversation) : ViewModelBase
{
    [ObservableProperty] private int depth;
    public Guid Id { get; } = conversation.Id;
    [ObservableProperty] private string title = conversation.Title;
    [ObservableProperty] private bool isCurrent;
    public string DisplayTitle => new string('　', Depth) + (Depth > 0 ? "↳ " : "") + (IsCurrent ? "● " : "") + Title;
    partial void OnDepthChanged(int value) => OnPropertyChanged(nameof(DisplayTitle));
    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(DisplayTitle));
    partial void OnIsCurrentChanged(bool value) => OnPropertyChanged(nameof(DisplayTitle));
}
