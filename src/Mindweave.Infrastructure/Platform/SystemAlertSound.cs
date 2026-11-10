using System.Runtime.InteropServices;
using Mindweave.Application.Notifications;

namespace Mindweave.Infrastructure.Platform;

/// <summary>
/// The system's standard alert: <c>NSBeep</c> (AppKit) on macOS, <c>MessageBeep(MB_OK)</c> on Windows. Other platforms
/// have no standard one here and stay silent. Which sound and how loud follow the system settings.
/// </summary>
public static partial class SystemAlertSound
{
    private const string AppKitPath = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const uint MbOk = 0x00000000;

    /// <summary>The alert for the platform the application runs on.</summary>
    public static IAlertSound ForPlatform() =>
        OperatingSystem.IsMacOS() ? new Sound(NSBeep)
        : OperatingSystem.IsWindows() ? new Sound(() => MessageBeep(MbOk))
        : new Sound(null);

    private sealed class Sound(Action? play) : IAlertSound
    {
        public void Play()
        {
            try
            {
                play?.Invoke();
            }
#pragma warning disable CA1031 // A missing sound is not worth an error: the visible hint is already shown.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }

    [LibraryImport(AppKitPath)]
    private static partial void NSBeep();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MessageBeep(uint type);
}
