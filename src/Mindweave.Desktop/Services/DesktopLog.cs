using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mindweave.Desktop.Services;

/// <summary>
/// Where the desktop layer's handled failures go (health check 2026-11-12, observability-2). Pages catch failures they
/// can live with (a preference that did not save, recommendations that did not load) and show a quiet message or a
/// default; before this they left nothing behind, so a recurring failure could not be diagnosed. Each such catch now
/// logs here, with where it happened. The application points it at its logger once its services exist.
/// </summary>
public static partial class DesktopLog
{
    private static ILogger s_logger = NullLogger.Instance;

    internal static void Use(ILogger logger) => s_logger = logger;

    /// <summary>A failure the page handled (it shows a default or a message); logged with the type and member it happened in.</summary>
    public static void Swallowed(Exception exception, [CallerMemberName] string member = "", [CallerFilePath] string file = "") =>
        LogSwallowed(s_logger, exception, Path.GetFileNameWithoutExtension(file), member);

    /// <summary>
    /// A message could not be drawn and is shown as plain text (D2 §2.5). Only the exception's type and where it was thrown
    /// are logged: its message can quote the text being drawn, and the log holds no conversation text.
    /// </summary>
    public static void LogDrawingFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        WriteDrawingFailure(s_logger, exception.GetType().FullName ?? exception.GetType().Name, exception.StackTrace ?? string.Empty);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A message could not be drawn and is shown as plain text: {Type}\n{Where}")]
    private static partial void WriteDrawingFailure(ILogger logger, string type, string where);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Handled failure in {Type}.{Member}")]
    private static partial void LogSwallowed(ILogger logger, Exception exception, string type, string member);
}
