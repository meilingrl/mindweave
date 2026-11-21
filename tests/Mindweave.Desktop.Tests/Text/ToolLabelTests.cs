using Mindweave.Application.Conversations;

namespace Mindweave.Desktop.Tests.Text;

/// <summary>
/// A third-party step is summarized "server › tool：input" (AnswerRunner.Summary); its label is the part before the
/// separator. A server's name is free text and may contain ": ", which must not cut the label short.
/// </summary>
public class ToolLabelTests
{
    [Fact]
    public void A_server_name_with_a_colon_is_kept_whole()
    {
        Assert.Equal("Notes: work › echo", UiText.ToolLabel("notes__echo", "Notes: work › echo：hello"));
    }

    [Fact]
    public void The_status_line_names_the_whole_server()
    {
        Assert.Equal("正在调用 Notes: work › echo…", UiText.AnswerStatus(AnswerStage.Tool, "notes__echo", "Notes: work › echo：hello"));
    }

    [Fact]
    public void An_English_summary_is_cut_at_its_separator()
    {
        Assert.Equal("files › echo", UiText.ToolLabel("files__echo", "files › echo: hello"));
    }
}
