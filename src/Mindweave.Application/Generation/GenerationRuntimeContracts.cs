namespace Mindweave.Application.Generation;

public enum GenerationPriority { Reply = 0, Summary = 1, Background = 2 }

/// <summary>App-owned participants retain execution and persistence tails independently of task status.</summary>
public interface IGenerationRuntimeParticipant
{
    bool HasActiveOrPendingWork { get; }
    Task ShutdownAsync();
    Task RepairPersistenceAsync();
    void ResumeAccepting();
}
