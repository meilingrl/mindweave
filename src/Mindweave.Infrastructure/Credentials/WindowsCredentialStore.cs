using System.Runtime.InteropServices;

namespace Mindweave.Infrastructure.Credentials;

internal static class WindowsCredentialStore
{
    internal static void Write(string reference, byte[] secret)
    {
        var pinned = GCHandle.Alloc(secret, GCHandleType.Pinned);
        try
        {
            var credential = new Credential
            {
                Type = 1, TargetName = reference, CredentialBlobSize = (uint)secret.Length,
                CredentialBlob = pinned.AddrOfPinnedObject(), Persist = 2, UserName = "Mindweave"
            };
            if (!CredWriteW(ref credential, 0)) ThrowLastError();
        }
        finally { pinned.Free(); }
    }

    internal static byte[] Read(string reference)
    {
        if (!CredReadW(reference, 1, 0, out var pointer)) ThrowLastError();
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlobSize > 2560) throw new CredentialVaultException(-1);
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { CredFree(pointer); }
    }

    internal static void Delete(string reference)
    {
        if (CredDeleteW(reference, 1, 0)) return;
        var error = Marshal.GetLastPInvokeError();
        if (error != 1168) throw new CredentialVaultException(error);
    }
    private static void ThrowLastError()
    {
        var error = Marshal.GetLastPInvokeError();
        throw new CredentialVaultException(error, error == 1168);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }
    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWriteW(ref Credential credential, uint flags);
    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDeleteW(string target, uint type, uint flags);
    [DllImport("Advapi32.dll", ExactSpelling = true)] private static extern void CredFree(IntPtr credential);
}
