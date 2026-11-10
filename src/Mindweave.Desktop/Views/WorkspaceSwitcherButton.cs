using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace Mindweave.Desktop.Views;

/// <summary>
/// The sidebar's workspace switcher (2026-11-10): a button styled as one (<see cref="StyleKeyOverride"/>), that also
/// tells a screen reader whether its menu is open, as the stock drop-down it replaced did (the expand/collapse pattern),
/// and lets one open or close it that way.
/// </summary>
public sealed class WorkspaceSwitcherButton : Button
{
    public static readonly StyledProperty<bool> IsMenuOpenProperty =
        AvaloniaProperty.Register<WorkspaceSwitcherButton, bool>(nameof(IsMenuOpen), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    private SwitcherPeer? _peer;

    /// <summary>The menu below is open (bound two-way to the shell's state).</summary>
    public bool IsMenuOpen
    {
        get => GetValue(IsMenuOpenProperty);
        set => SetValue(IsMenuOpenProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(Button);

    protected override AutomationPeer OnCreateAutomationPeer() => _peer = new SwitcherPeer(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsMenuOpenProperty)
        {
            _peer?.StateChanged(change.GetOldValue<bool>(), change.GetNewValue<bool>());
        }
    }

    private sealed class SwitcherPeer(WorkspaceSwitcherButton owner) : ButtonAutomationPeer(owner), IExpandCollapseProvider
    {
        public ExpandCollapseState ExpandCollapseState => State(owner.IsMenuOpen);

        public bool ShowsMenu => true;

        public void Expand() => owner.IsMenuOpen = true;

        public void Collapse() => owner.IsMenuOpen = false;

        internal void StateChanged(bool was, bool now) =>
            RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty, State(was), State(now));

        private static ExpandCollapseState State(bool open) => open ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
    }
}
