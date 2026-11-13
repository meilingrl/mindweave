using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Mindweave.Desktop.ViewModels;

namespace Mindweave.Desktop.Services;

/// <summary>
/// The last line of defence on the UI thread (health check 2026-11-12, item 3). A command that throws something its
/// page does not handle would otherwise leave the dispatcher loop and end the application, taking unsaved text and any
/// answer kept for a later write with it. Here the exception is logged, the shell says an operation did not complete,
/// and the loop goes on. Exceptions after which the process cannot be trusted are left to end it.
/// </summary>
internal sealed partial class UiExceptionGuard : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<ShellViewModel?> _shell;
    private readonly ILogger _logger;

    public UiExceptionGuard(Dispatcher dispatcher, Func<ShellViewModel?> shell, ILogger logger)
    {
        _dispatcher = dispatcher;
        _shell = shell;
        _logger = logger;
        _dispatcher.UnhandledException += OnUnhandledException;
    }

    public void Dispose() => _dispatcher.UnhandledException -= OnUnhandledException;

    internal static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or InsufficientExecutionStackException or AccessViolationException or System.Runtime.InteropServices.SEHException;

    private void OnUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (IsFatal(e.Exception))
        {
            LogFatal(_logger, e.Exception);
            return;
        }

        LogCaught(_logger, e.Exception);
        e.Handled = true;
        try
        {
            _shell()?.ShowRestoreNotice(UiText.UnexpectedError, isError: true);
        }
#pragma warning disable CA1031 // Between two sessions the shell may be gone; the exception is logged either way.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception on the UI thread; the application goes on")]
    private static partial void LogCaught(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Fatal exception on the UI thread")]
    private static partial void LogFatal(ILogger logger, Exception exception);
}
