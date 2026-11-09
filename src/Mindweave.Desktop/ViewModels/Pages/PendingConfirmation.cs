namespace Mindweave.Desktop.ViewModels.Pages;

/// <summary>
/// A question the page asks before an irreversible or replacing step; <see cref="Proceed"/> runs on "confirm".
/// <see cref="IsDestructive"/> marks a step that removes something (deleting versions, discarding the candidate), whose
/// confirm button is drawn in the danger style; adopting only replaces the current version, which stays in the history.
/// </summary>
public sealed record PendingConfirmation(string Message, Func<Task> Proceed, bool IsDestructive = false);
