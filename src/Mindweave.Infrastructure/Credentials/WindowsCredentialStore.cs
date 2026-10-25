using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Mindweave.Application.Credentials;

namespace Mindweave.Infrastructure.Credentials;

/// <summary>Generic credentials in Windows Credential Manager; target names are prefixed per channel.</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsCredentialStore(string prefix) : ICredentialStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public Task<string?> ReadAsync(string reference, CancellationToken cancellationToken) =>
        Task.Run(() => Read(Target(reference)), cancellationToken);

    public Task WriteAsync(string reference, string secret, CancellationToken cancellationToken) =>
        Task.Run(() => Write(Target(reference), secret), cancellationToken);

    public Task DeleteAsync(string reference, CancellationToken cancellationToken) =>
        Task.Run(() => Delete(Target(reference)), cancellationToken);

    private string Target(string reference) => $"{prefix}/{reference}";

    private static string? Read(string target)
    {
        if (!CredReadW(target, CredTypeGeneric, 0, out var handle))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == ErrorNotFound ? null : throw Unavailable("read", error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(handle);
            if (credential.CredentialBlobSize == 0)
            {
                return string.Empty;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CredFree(handle);
        }
    }

    private static void Write(string target, string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        var targetName = Marshal.StringToHGlobalUni(target);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = targetName,
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
            };
            if (!CredWriteW(ref credential, 0))
            {
                throw Unavailable("write", Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            // Clear the plain-text copy before freeing it.
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
            Marshal.FreeHGlobal(targetName);
        }
    }

    private static void Delete(string target)
    {
        if (!CredDeleteW(target, CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != ErrorNotFound)
            {
                throw Unavailable("delete", error);
            }
        }
    }

    private static CredentialUnavailableException Unavailable(string operation, int error) =>
        new($"Credential Manager {operation} failed (Win32 error {error}).");

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredReadW(string target, int type, int flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWriteW(ref Credential credential, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDeleteW(string target, int type, int flags);

    [LibraryImport("advapi32.dll")]
    private static partial void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }
}
