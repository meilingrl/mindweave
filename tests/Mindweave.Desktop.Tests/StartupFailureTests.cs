using System.Reflection;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Startup;
using Mindweave.Desktop.ViewModels;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Health check 2026-11-12, review of the fixes: a start-up step that throws something unforeseen shows the recovery
/// page with what went wrong, instead of leaving the start page's spinner turning forever.
/// </summary>
public class StartupFailureTests
{
    [AvaloniaFact]
    public async Task An_unexpected_failure_during_start_up_shows_the_recovery_page()
    {
        await using var app = DesktopHarness.Create(s => s.AddSingleton<IDatabaseLifecycle>(sp => ThrowingOpen.Wrap(sp.GetRequiredService<SqliteDatabase>())));
        app.ShowWindow();

        await app.StartAsync();

        var recovery = Assert.IsType<RecoveryViewModel>(app.Root.Current);
        Assert.Equal(DatabaseOpenStatus.IoError, recovery.Result.Status);
        Assert.Contains("simulated", recovery.Result.ErrorDetail, StringComparison.Ordinal);
    }

    /// <summary>The real database, whose open throws an exception no caller expects.</summary>
    public class ThrowingOpen : DispatchProxy
    {
        private IDatabaseLifecycle _inner = null!;

        public static IDatabaseLifecycle Wrap(IDatabaseLifecycle inner)
        {
            var proxy = Create<IDatabaseLifecycle, ThrowingOpen>();
            ((ThrowingOpen)(object)proxy)._inner = inner;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            return targetMethod.Name == nameof(IDatabaseLifecycle.OpenAsync)
                ? Task.FromException<DatabaseOpenResult>(new InvalidOperationException("simulated start-up defect"))
                : targetMethod.Invoke(_inner, args);
        }
    }
}
