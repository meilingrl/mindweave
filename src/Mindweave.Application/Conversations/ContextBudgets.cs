using Microsoft.Extensions.Logging;
using Mindweave.Application.Diagnostics;
using Mindweave.Core.Context;
using Mindweave.Core.Models;

namespace Mindweave.Application.Conversations;

/// <summary>Context budgets for planning and compressing requests, calibrated from what recent requests were billed.</summary>
public sealed partial class ContextBudgets(IAiRequestStore requests, ILogger<ContextBudgets> logger)
{
    /// <summary>
    /// The model's context budget, with the calibration learned from what this address and model billed for recent
    /// requests (<see cref="TokenCalibration"/>). If the requests cannot be read, the plain estimate is used: requests
    /// are not held up for a diagnostic table, and the budget's margin still applies.
    /// </summary>
    public async Task<ContextBudget> BudgetForAsync(ModelSnapshot model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        var calibration = TokenCalibration.None;
        try
        {
            var samples = await requests.RecentTokenSamplesAsync(model.BaseUrl, model.Model, TokenCalibration.SampleSize, cancellationToken).ConfigureAwait(false);
            calibration = TokenCalibration.Fit([.. samples.Select(s => (s.Estimate, s.Reported))]);
        }
#pragma warning disable CA1031 // Learned counts only refine the budget; without them the plain estimate is used.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogCalibrationFailed(logger, ex, model.Model);
        }

        return ContextBudget.For(model, calibration);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Token counts for model {Model} could not be read; the plain estimate is used")]
    private static partial void LogCalibrationFailed(ILogger logger, Exception exception, string model);
}
