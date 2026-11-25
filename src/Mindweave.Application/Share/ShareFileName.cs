using System.Globalization;
using Mindweave.Application.Export;

namespace Mindweave.Application.Share;

/// <summary>The file name the save dialog suggests for a share: its title and the date (docs/design/sharing.md S10).</summary>
public static class ShareFileName
{
    /// <summary>Without an extension; a title with nothing a file name may hold leaves the date alone.</summary>
    public static string Suggest(string title, DateOnly day)
    {
        var date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var name = ExportFileName.Clean(title, room: date.Length + 1);
        return name.Length == 0 ? date : ExportFileName.Safe(name + " " + date);
    }
}
