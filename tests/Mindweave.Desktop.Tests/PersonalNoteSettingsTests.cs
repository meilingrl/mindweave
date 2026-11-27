using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Preferences;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Pages;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// 设置 › 个人说明 (requirements §10.9): typed text is saved as it is typed, the box takes no more than the limit, the count
/// says how much is used, and the find box finds the section.
/// </summary>
public class PersonalNoteSettingsTests
{
    [AvaloniaFact]
    public async Task Typing_saves_the_note_and_the_box_takes_no_more_than_the_limit()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720, ThemeVariant.Light);
        await app.StartWithWorkspaceAsync("火车");
        app.Workbench.Navigate(new NavigationRequest(MainSection.Settings));
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await settings.Note!.Loaded;
        window.UpdateLayout();

        var box = Find<TextBox>(window, "PersonalNoteBox");
        Assert.Equal(PersonalNoteService.MaxLength, box.MaxLength);
        Assert.Equal("0 / 500", Find<TextBlock>(window, "PersonalNoteCount").Text);

        box.Focus();
        window.KeyTextInput("我是护士");
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await settings.Note.Saving;
        Assert.Equal("我是护士", await app.Services.GetRequiredService<IPersonalNote>().GetAsync(CancellationToken.None));
        Assert.Equal("4 / 500", Find<TextBlock>(window, "PersonalNoteCount").Text);

        // Up to the limit, then nothing more is taken; the count says so in the warning colour.
        box.Text = new string('注', PersonalNoteService.MaxLength - 1);
        box.CaretIndex = box.Text.Length;
        window.KeyTextInput("满了还要");
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await settings.Note.Saving;
        Assert.Equal(PersonalNoteService.MaxLength, box.Text!.Length);
        Assert.EndsWith("注满", box.Text, StringComparison.Ordinal);
        Assert.True(settings.Note.IsFull);
        var count = Find<TextBlock>(window, "PersonalNoteCount");
        Assert.Equal(UiText.PersonalNoteFull(PersonalNoteService.MaxLength), count.Text);
        Assert.Contains("warning", count.Classes);
        Assert.Equal(box.Text, await app.Services.GetRequiredService<IPersonalNote>().GetAsync(CancellationToken.None));
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_find_box_finds_the_note_and_its_category_jumps_to_it()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720, ThemeVariant.Light);
        await app.StartWithWorkspaceAsync("火车");
        app.Workbench.Navigate(new NavigationRequest(MainSection.Settings));
        await Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage).Loaded;
        window.UpdateLayout();

        Find<TextBox>(window, "FindSettingsBox").Text = UiText.PersonalNoteSection;
        await SettleAsync(window);
        Assert.True(Find<TextBox>(window, "PersonalNoteBox").IsEffectivelyVisible);
        Assert.False(Find<StackPanel>(window, "DataSection").IsEffectivelyVisible);
        Find<TextBox>(window, "FindSettingsBox").Text = string.Empty;
        await SettleAsync(window);

        UiInput.Click(window, Find<RadioButton>(window, "CategoryNote"));
        await SettleAsync(window);
        Assert.True(Find<RadioButton>(window, "CategoryNote").IsChecked);
        var top = Find<StackPanel>(window, "NoteSection").TranslatePoint(default, Find<ScrollViewer>(window, "Scroller"))!.Value.Y;
        Assert.InRange(top, -1, 48);
        window.Close();
    }

    private static async Task SettleAsync(Window window)
    {
        for (var settle = 0; settle < 4; settle++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
        }
    }
}
