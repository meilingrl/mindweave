using Avalonia.Headless.XUnit;
using Mindweave.Desktop.ViewModels;

namespace Mindweave.Desktop.Tests;

public class WorkspaceNameFormTests
{
    [AvaloniaFact]
    public async Task A_name_that_could_not_be_saved_stays_typed_and_the_error_does_not_show_the_exception_text()
    {
        const string internals = "SQLite Error 14: 'unable to open database file'.";
        var form = new WorkspaceNameForm(_ => Task.FromException(new IOException(internals))) { Name = "火车" };

        await form.SubmitCommand.ExecuteAsync(null);

        Assert.Equal(UiText.SaveFailed, form.Error);
        Assert.DoesNotContain(internals, form.Error, StringComparison.Ordinal);
        Assert.Equal("火车", form.Name);
    }
}
