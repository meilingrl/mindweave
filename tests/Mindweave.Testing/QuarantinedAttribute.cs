using Xunit.v3;

namespace Mindweave.Testing;

/// <summary>
/// Marks a test that fails intermittently for a reason not yet fixed. It keeps running, but in a separate step that
/// reports without blocking (scripts/test.sh, CI), so a red run means a real failure. A quarantine is temporary: the
/// test is fixed in the next repair batch and the attribute removed; STATUS.md lists every open one
/// (docs/design/testing.md).
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class QuarantinedAttribute : Attribute, ITraitAttribute
{
    /// <param name="since">The day it was quarantined, yyyy-MM-dd.</param>
    /// <param name="reason">What fails and where it was seen, for whoever fixes it.</param>
    public QuarantinedAttribute(string since, string reason)
    {
        Since = since;
        Reason = reason;
    }

    public string Since { get; }

    public string Reason { get; }

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() =>
        [new("Category", "Quarantine"), new("QuarantinedSince", Since)];
}
