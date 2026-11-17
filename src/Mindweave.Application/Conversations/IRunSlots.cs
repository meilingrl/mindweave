using Mindweave.Core.Conversations;

namespace Mindweave.Application.Conversations;

/// <summary>
/// A run's execution slot while it waits for the user (architecture §15.5, decision W10): released while an approval card
/// waits, so other work can run, and taken back, ahead of queued work, once the user has decided.
/// </summary>
internal interface IRunSlots
{
    /// <summary>The run gives its slot up; it still counts as unfinished work.</summary>
    void Release(GenerationTaskId task);

    /// <summary>Waits for a free slot and takes it. Cancelled with the run.</summary>
    Task ReacquireAsync(GenerationTaskId task, CancellationToken cancellationToken);
}
