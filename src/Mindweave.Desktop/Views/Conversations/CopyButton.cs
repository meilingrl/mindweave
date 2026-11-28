using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Mindweave.Desktop.Views.Conversations;

/// <summary>
/// An icon-only "copy" button (S7 round 3): puts <see cref="Text"/> on the window's clipboard and shows a check for a
/// moment as confirmation. Styled as a plain button, so the shared button classes apply to it.
/// </summary>
public sealed class CopyButton : Button
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<CopyButton, string?>(nameof(Text));

    /// <summary>A word beside the icon ("Copy details"); without it the button is the icon alone.</summary>
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<CopyButton, string?>(nameof(Label));

    /// <summary>How long the check stays after a copy.</summary>
    internal static readonly TimeSpan ConfirmationTime = TimeSpan.FromSeconds(1.5);

    private readonly PathIcon _icon = new();
    private readonly DispatcherTimer _revert;

    public CopyButton()
    {
        Content = _icon;
        _revert = new DispatcherTimer { Interval = ConfirmationTime };
        _revert.Tick += (_, _) => ShowCopied(false);
        ToolTip.SetTip(this, UiText.Copy);
        Avalonia.Automation.AutomationProperties.SetName(this, UiText.Copy);
        UpdateIcon();
    }

    /// <summary>The exact text to copy (a message's Markdown source).</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>True for a moment after a successful copy, while the check is shown.</summary>
    internal bool ShowsCopied { get; private set; }

    protected override Type StyleKeyOverride => typeof(Button);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelProperty)
        {
            Content = null; // The icon leaves the button before it joins the row (one logical parent at a time).
            if (Label is { } label)
            {
                Content = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 5,
                    Children = { _icon, new TextBlock { Text = label, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center } },
                };
                Avalonia.Automation.AutomationProperties.SetName(this, label);
            }
            else
            {
                Content = _icon;
            }
        }
    }

    protected override async void OnClick()
    {
        base.OnClick();
        if (string.IsNullOrEmpty(Text) || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(Text);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or TimeoutException)
        {
            // The system clipboard can be held by another program for a moment; nothing was copied, so no check.
            return;
        }

        ShowCopied(true);
        _revert.Stop();
        _revert.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _revert.Stop();
        ShowCopied(false);
        base.OnDetachedFromVisualTree(e);
    }

    private void ShowCopied(bool copied)
    {
        _revert.Stop();
        ShowsCopied = copied;
        PseudoClasses.Set(":copied", copied);
        ToolTip.SetTip(this, copied ? UiText.Copied : Label ?? UiText.Copy);
        UpdateIcon();
    }

    private void UpdateIcon()
    {
        // Resource observables, not lookups: the glyphs are application resources, reachable only once attached.
        _icon.Bind(PathIcon.DataProperty, this.GetResourceObservable(ShowsCopied ? "IconCheck" : "IconCopy"));

        // The check is in the accent colour; otherwise the icon follows the button (muted at rest, stronger on hover).
        if (ShowsCopied)
        {
            _icon.Bind(ForegroundProperty, this.GetResourceObservable("AccentBrush"));
        }
        else
        {
            _icon.ClearValue(ForegroundProperty);
        }
    }
}
