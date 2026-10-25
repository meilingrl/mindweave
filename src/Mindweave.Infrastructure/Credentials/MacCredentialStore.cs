using System.Runtime.InteropServices;
using System.Text;

namespace Mindweave.Infrastructure.Credentials;

internal static class MacCredentialStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static readonly object InteractionGate = new();

    // No access controls are changed. Suppress optional UI while performing an operation,
    // then restore the exact prior process setting; denied/locked items produce a status.
    private static T WithoutInteraction<T>(Func<T> operation)
    {
        lock (InteractionGate)
        {
            Check(SecKeychainGetUserInteractionAllowed(out var previous));
            Check(SecKeychainSetUserInteractionAllowed(0));
            try { return operation(); }
            finally { Check(SecKeychainSetUserInteractionAllowed(previous)); }
        }
    }

    internal static void Write(string service, string account, byte[] secret) => WithoutInteraction(() =>
    {
        var serviceBytes = Encoding.UTF8.GetBytes(service);
        var accountBytes = Encoding.UTF8.GetBytes(account);
        Check(SecKeychainAddGenericPassword(IntPtr.Zero, (uint)serviceBytes.Length, serviceBytes,
            (uint)accountBytes.Length, accountBytes, (uint)secret.Length, secret, IntPtr.Zero));
        return true;
    });

    internal static byte[] Read(string service, string account) => WithoutInteraction(() =>
    {
        var serviceBytes = Encoding.UTF8.GetBytes(service);
        var accountBytes = Encoding.UTF8.GetBytes(account);
        IntPtr data = IntPtr.Zero;
        try
        {
            Check(SecKeychainFindGenericPassword(IntPtr.Zero, (uint)serviceBytes.Length, serviceBytes,
                (uint)accountBytes.Length, accountBytes, out var length, out data, IntPtr.Zero));
            if (length > 2560) throw new CredentialVaultException(-1);
            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { if (data != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, data); }
    });

    internal static void Delete(string service, string account) => WithoutInteraction(() =>
    {
        var serviceBytes = Encoding.UTF8.GetBytes(service);
        var accountBytes = Encoding.UTF8.GetBytes(account);
        IntPtr item = IntPtr.Zero;
        try
        {
            var status = FindItem(IntPtr.Zero, (uint)serviceBytes.Length, serviceBytes,
                (uint)accountBytes.Length, accountBytes, IntPtr.Zero, IntPtr.Zero, out item);
            if (status == -25300) return true;
            Check(status);
            Check(SecKeychainItemDelete(item));
            return true;
        }
        finally { if (item != IntPtr.Zero) CFRelease(item); }
    });
    private static void Check(int status) { if (status != 0) throw new CredentialVaultException(status, status == -25300); }

    [DllImport(Security)] private static extern int SecKeychainGetUserInteractionAllowed(out byte allowed);
    [DllImport(Security)] private static extern int SecKeychainSetUserInteractionAllowed(byte allowed);
    [DllImport(Security)] private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, uint length, byte[] data, IntPtr item);
    [DllImport(Security)] private static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, out uint length, out IntPtr data, IntPtr item);
    [DllImport(Security, EntryPoint = "SecKeychainFindGenericPassword")] private static extern int FindItem(IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, IntPtr length, IntPtr data, out IntPtr item);
    [DllImport(Security)] private static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);
    [DllImport(Security)] private static extern int SecKeychainItemDelete(IntPtr item);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr item);
}
