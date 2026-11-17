using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels.Conversations;

/// <summary>
/// 新探索 while no model is connected (2026-11-17): a workspace exists but the guide's model step was left for later, or
/// the settings were never made. Says so before a question is typed, with the way to 设置; it goes as soon as settings
/// are saved, wherever.
/// </summary>
public sealed partial class ModelConnectionNote : ViewModelBase, IDisposable
{
    private readonly IModelSettings _models;
    private readonly IConversations _conversations;
    private readonly IUiDispatcher? _dispatcher;
    private bool _disposed;

    public ModelConnectionNote(IModelSettings models, IConversations conversations, IUiDispatcher? dispatcher)
    {
        _models = models;
        _conversations = conversations;
        _dispatcher = dispatcher;
        conversations.ModelChanged += OnModelChanged;
        Checked = CheckAsync();
    }

    /// <summary>No model settings are saved.</summary>
    [ObservableProperty]
    public partial bool IsShown { get; private set; }

    /// <summary>The latest check (tests).</summary>
    internal Task Checked { get; private set; }

    public void Dispose()
    {
        _disposed = true;
        _conversations.ModelChanged -= OnModelChanged;
    }

    private void OnModelChanged(object? sender, EventArgs e)
    {
        if (_dispatcher is null)
        {
            Checked = CheckAsync();
        }
        else
        {
            _dispatcher.Post(() => Checked = CheckAsync());
        }
    }

    private async Task CheckAsync()
    {
        bool missing;
        try
        {
            missing = await _models.GetAsync(CancellationToken.None) is null;
        }
#pragma warning disable CA1031 // A hint: when the settings cannot be read, sending says what is wrong.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
            missing = false;
        }

        if (!_disposed)
        {
            IsShown = missing;
        }
    }
}
