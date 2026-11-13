using Avalonia.Headless.XUnit;
using Mindweave.Desktop.ViewModels.Conversations;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Health check 2026-11-12, requirements-3: the first question typed on 新探索 and not sent is kept like any other
/// draft (§5.1, §9.3): going back, opening the page again and a restart all find it; sending it clears it.
/// </summary>
public class NewExplorationDraftTests
{
    [AvaloniaFact]
    public async Task An_unsent_first_question_survives_leaving_the_page_and_a_restart_and_sending_clears_it()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow();
        await app.StartWithWorkspaceAsync("火车");
        var page = await OpenNewAsync(app);
        page.Composer.Text = "一个很长、还没发出去的问题";

        app.Workbench.GoBack();
        page = await OpenNewAsync(app);
        Assert.Equal("一个很长、还没发出去的问题", page.Composer.Text);

        await app.RestartAsync();
        page = await OpenNewAsync(app);
        Assert.Equal("一个很长、还没发出去的问题", page.Composer.Text);

        await app.ConfigureModelAsync();
        await page.Composer.SendCommand.ExecuteAsync(null);
        Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        page = await OpenNewAsync(app);
        Assert.Equal(string.Empty, page.Composer.Text);
    }

    private static async Task<NewConversationViewModel> OpenNewAsync(DesktopHarness app)
    {
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var page = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        await page.DraftLoaded;
        return page;
    }
}
