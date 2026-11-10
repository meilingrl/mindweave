namespace Mindweave.Application.Notifications;

/// <summary>
/// The platform's standard short alert sound (2026-11-10): the system beep on macOS and Windows, nothing elsewhere.
/// Behind an interface so tests count calls instead of making noise.
/// </summary>
public interface IAlertSound
{
    /// <summary>Plays the alert once; never throws.</summary>
    void Play();
}
