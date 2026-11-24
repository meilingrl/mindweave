using Avalonia.Data.Converters;
using Mindweave.Application.Conversations;
using Mindweave.Desktop.Views;

namespace Mindweave.Desktop;

public static class Converters
{
    public static FuncValueConverter<bool, double> SupersededOpacity { get; } = new(superseded => superseded ? 0.55 : 1.0);

    /// <summary>Unread names are bold (S7 已读／未读约定).</summary>
    public static FuncValueConverter<bool, Avalonia.Media.FontWeight> BoldWhen { get; } = new(bold => bold ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal);

    public static FuncValueConverter<int, bool> IsPositive { get; } = new(count => count > 0);

    /// <summary>The read toggle says what pressing it does.</summary>
    public static FuncValueConverter<bool, string> ReadToggleLabel { get; } = new(read => read ? UiText.MarkUnread : UiText.MarkRead);

    /// <summary>The database lives in the data directory shown just above it, so its row shows the file name (full path on hover).</summary>
    public static FuncValueConverter<string?, string> FileName { get; } = new(path => Path.GetFileName(path) ?? string.Empty);

    /// <summary>
    /// A running answer's weaving motion from its stage (docs/design/reasoning-and-status.md §2): the braid while waiting,
    /// retrying or finishing; joining while thinking; tightening while compressing; reaching while a tool fetches; holding
    /// while an approval waits; the thread once text streams in.
    /// </summary>
    public static FuncValueConverter<AnswerStage, WeavingMotion> AnswerMotion { get; } = new(stage => stage switch
    {
        AnswerStage.Thinking => WeavingMotion.Joining,
        AnswerStage.Compressing => WeavingMotion.Tightening,
        AnswerStage.Tool => WeavingMotion.Reaching,
        AnswerStage.Approval => WeavingMotion.Holding,
        AnswerStage.Writing or AnswerStage.Done => WeavingMotion.Thread,
        _ => WeavingMotion.Braid,
    });
}
