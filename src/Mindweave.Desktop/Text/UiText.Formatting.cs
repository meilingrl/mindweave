using System.Globalization;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Desktop.Text;

// Times, durations and sizes as the interface writes them everywhere.
public static partial class UiText
{
    /// <summary>A file size for reading: KB below one megabyte, MB with one decimal above.</summary>
    public static string FormatSize(long bytes) => bytes < 1024 * 1024
        ? T(
            string.Format(CultureInfo.GetCultureInfo("en-US"), "{0:N0} KB", Math.Max(1, (bytes + 1023) / 1024)),
            string.Format(CultureInfo.CurrentCulture, "{0:N0} KB", Math.Max(1, (bytes + 1023) / 1024)))
        : T(
            string.Format(CultureInfo.GetCultureInfo("en-US"), "{0:N1} MB", bytes / (1024.0 * 1024.0)),
            string.Format(CultureInfo.CurrentCulture, "{0:N1} MB", bytes / (1024.0 * 1024.0)));

    /// <summary>
    /// A moment as the interface shows it everywhere (S7): local time, the language's date and a 24-hour clock without
    /// AM/PM, for example "Mar 14, 2026 09:30" or "2026年3月14日 09:30", the same whatever the machine's regional format.
    /// </summary>
    public static string FormatTime(DateTimeOffset at) => T(
        at.ToLocalTime().ToString("MMM d, yyyy HH:mm", CultureInfo.GetCultureInfo("en-US")),
        at.ToLocalTime().ToString("yyyy'年'M'月'd'日' HH:mm", CultureInfo.InvariantCulture));

    /// <summary>
    /// When a message was sent or answered: only the clock ("09:30") on the day it is shown, otherwise the full
    /// <see cref="FormatTime"/> date. Both moments are compared as local calendar days.
    /// </summary>
    public static string MessageTime(DateTimeOffset at, DateTimeOffset now) =>
        at.ToLocalTime().Date == now.ToLocalTime().Date
            ? at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
            : FormatTime(at);

    /// <summary>
    /// How long an answer has been running: "12 s", "1 min 05 s", "1 h 02 min" / "12 秒", "1 分 05 秒", "1 小时 02 分"
    /// (whole seconds, never negative).
    /// </summary>
    public static string Elapsed(TimeSpan span)
    {
        var seconds = (long)Math.Max(0, Math.Floor(span.TotalSeconds));
        return seconds < 60 ? T(string.Format(CultureInfo.InvariantCulture, "{0} s", seconds), string.Format(CultureInfo.InvariantCulture, "{0} 秒", seconds))
            : seconds < 3600 ? T(string.Format(CultureInfo.InvariantCulture, "{0} min {1:00} s", seconds / 60, seconds % 60), string.Format(CultureInfo.InvariantCulture, "{0} 分 {1:00} 秒", seconds / 60, seconds % 60))
            : T(string.Format(CultureInfo.InvariantCulture, "{0} h {1:00} min", seconds / 3600, seconds % 3600 / 60), string.Format(CultureInfo.InvariantCulture, "{0} 小时 {1:00} 分", seconds / 3600, seconds % 3600 / 60));
    }

    /// <summary>
    /// A finished answer's total time beside its clock time: "Took 14 s" / "用时 14 秒"; "Took under 1 s" / "用时不到 1 秒"
    /// for an instant one.
    /// </summary>
    public static string AnswerDuration(TimeSpan span) => span < TimeSpan.FromSeconds(1)
        ? T("Took under 1 s", "用时不到 1 秒")
        : T("Took " + Elapsed(span), "用时 " + Elapsed(span));
}
