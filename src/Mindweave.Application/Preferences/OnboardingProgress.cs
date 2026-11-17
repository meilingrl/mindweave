using Mindweave.Application.Storage;

namespace Mindweave.Application.Preferences;

/// <summary>Where first-run onboarding (首次引导, 2026-11-17) stands, in the order it is shown.</summary>
public enum OnboardingStep
{
    /// <summary>What Mindweave is for.</summary>
    Welcome,

    /// <summary>Connect a model (may be left for later).</summary>
    Model,

    /// <summary>Create the first workspace.</summary>
    Workspace,

    /// <summary>Finished or skipped: the guide is never shown again; a missing workspace is only asked for.</summary>
    Done,
}

/// <summary>
/// The guide's progress, kept so a restart resumes at the step it left and a finished guide is never shown again
/// (requirements §6.1 item 1, 2026-11-17).
/// </summary>
public interface IOnboardingProgress
{
    Task<OnboardingStep> GetAsync(CancellationToken cancellationToken);

    Task SetAsync(OnboardingStep reached, CancellationToken cancellationToken);
}

/// <summary>
/// One key in the preference store (no migration). A missing or unknown value means the guide has not started. The step
/// is also held in memory once read or set, so a step that could not be written still holds until the application closes.
/// </summary>
public sealed class OnboardingProgressService(IPreferenceStore store) : IOnboardingProgress
{
    public const string Key = "onboarding.step";

    private OnboardingStep? _step;

    public async Task<OnboardingStep> GetAsync(CancellationToken cancellationToken)
    {
        if (_step is { } known)
        {
            return known;
        }

        var stored = await store.GetAsync(Key, cancellationToken).ConfigureAwait(false) switch
        {
            "model" => OnboardingStep.Model,
            "workspace" => OnboardingStep.Workspace,
            "done" => OnboardingStep.Done,
            _ => OnboardingStep.Welcome,
        };

        // A step set while this was reading wins over what was stored before it.
        return _step ??= stored;
    }

    public Task SetAsync(OnboardingStep reached, CancellationToken cancellationToken)
    {
        _step = reached;
        return store.SetAsync(Key, reached switch
        {
            OnboardingStep.Welcome => "welcome",
            OnboardingStep.Model => "model",
            OnboardingStep.Workspace => "workspace",
            _ => "done",
        }, cancellationToken);
    }
}
