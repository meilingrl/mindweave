using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Core.Chat;
namespace Mindweave.Desktop.ViewModels;
public sealed partial class ConversationItemViewModel(Conversation conversation) : ViewModelBase
{
    public Guid Id { get; } = conversation.Id;
    [ObservableProperty] private string title = conversation.Title;
    [ObservableProperty] private bool isCurrent;
    public string DisplayTitle => (IsCurrent ? "● " : "") + Title;
    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(DisplayTitle));
    partial void OnIsCurrentChanged(bool value) => OnPropertyChanged(nameof(DisplayTitle));
}
