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

    /// <param name="watchRefreshes">Also report failed page reads (<see cref="SerialRefresh.Failed"/>, process-wide). Tests that
    /// run several applications in one process turn it off, so one test's failed read does not show in another's shell.</param>
    public UiExceptionGuard(Dispatcher dispatcher, Func<ShellViewModel?> shell, ILogger logger, bool watchRefreshes = true)
    {
        _dispatcher = dispatcher;
        _shell = shell;
        _logger = logger;
        _dispatcher.UnhandledException += OnUnhandledException;
        if (watchRefreshes)
        {
            SerialRefresh.Failed += OnRefreshFailed;
        }
    }

    public void Dispose()
    {
        _dispatcher.UnhandledException -= OnUnhandledException;
        SerialRefresh.Failed -= OnRefreshFailed;
    }

    /// <summary>A page could not read what it shows: logged, and the shell says so; the page keeps what it showed.</summary>
    private void OnRefreshFailed(object? sender, Exception exception)
    {
        LogRefreshFailed(_logger, exception);
        try
        {
            _shell()?.ShowRestoreNotice(UiText.LoadFailed, isError: true);
        }
#pragma warning disable CA1031 // Between two sessions the shell may be gone; the failure is logged either way.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A page could not read its content")]
    private static partial void LogRefreshFailed(ILogger logger, Exception exception);

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
