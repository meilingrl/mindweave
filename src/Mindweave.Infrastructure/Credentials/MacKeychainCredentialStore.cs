using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Mindweave.Application.Credentials;

namespace Mindweave.Infrastructure.Credentials;

/// <summary>
/// Generic-password items in the user's login keychain via Security.framework (SecItem API).
/// Service name is per channel, so tracks and builds never share secrets; the account is the reference.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed partial class MacKeychainCredentialStore(string service) : ICredentialStore
{
    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;
    private const int ErrSecDuplicateItem = -25299;

    private const string SecurityPath = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationPath = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint KCFStringEncodingUTF8 = 0x08000100;

    private static readonly Lazy<Constants> Keys = new(Constants.Load);

    public Task<string?> ReadAsync(string reference, CancellationToken cancellationToken) =>
        Task.Run(() => Read(reference), cancellationToken);

    public Task WriteAsync(string reference, string secret, CancellationToken cancellationToken) =>
        Task.Run(() => Write(reference, secret), cancellationToken);

    public Task DeleteAsync(string reference, CancellationToken cancellationToken) =>
        Task.Run(() => Delete(reference), cancellationToken);

    private string? Read(string reference)
    {
        var k = Keys.Value;
        using var scope = new CfScope();
        var query = scope.Dictionary(
            (k.Class, k.ClassGenericPassword),
            (k.AttrService, scope.String(service)),
            (k.AttrAccount, scope.String(reference)),
            (k.ReturnData, k.True),
            (k.MatchLimit, k.MatchLimitOne));
        var status = SecItemCopyMatching(query, out var data);
        if (status == ErrSecItemNotFound)
        {
            return null;
        }

        Check(status, "read");
        try
        {
            var length = (int)CFDataGetLength(data);
            var bytes = new byte[length];
            Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CFRelease(data);
        }
    }

    private void Write(string reference, string secret)
    {
        var k = Keys.Value;
        using var scope = new CfScope();
        var value = scope.Data(Encoding.UTF8.GetBytes(secret));
        var item = scope.Dictionary(
            (k.Class, k.ClassGenericPassword),
            (k.AttrService, scope.String(service)),
            (k.AttrAccount, scope.String(reference)),
            (k.AttrLabel, scope.String($"{service} ({reference})")),
            (k.ValueData, value));
        var status = SecItemAdd(item, IntPtr.Zero);
        if (status == ErrSecDuplicateItem)
        {
            var query = scope.Dictionary(
                (k.Class, k.ClassGenericPassword),
                (k.AttrService, scope.String(service)),
                (k.AttrAccount, scope.String(reference)));
            status = SecItemUpdate(query, scope.Dictionary((k.ValueData, value)));
        }

        Check(status, "write");
    }

    private void Delete(string reference)
    {
        var k = Keys.Value;
        using var scope = new CfScope();
        var status = SecItemDelete(scope.Dictionary(
            (k.Class, k.ClassGenericPassword),
            (k.AttrService, scope.String(service)),
            (k.AttrAccount, scope.String(reference))));
        if (status != ErrSecItemNotFound)
        {
            Check(status, "delete");
        }
    }

    private static void Check(int status, string operation)
    {
        if (status != ErrSecSuccess)
        {
            // Locked keychain, denied access or a missing keychain all mean: unavailable, never a plain-text fallback.
            throw new CredentialUnavailableException($"Keychain {operation} failed (OSStatus {status}).");
        }
    }

    [LibraryImport(SecurityPath)]
    private static partial int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [LibraryImport(SecurityPath)]
    private static partial int SecItemAdd(IntPtr attributes, IntPtr result);

    [LibraryImport(SecurityPath)]
    private static partial int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);

    [LibraryImport(SecurityPath)]
    private static partial int SecItemDelete(IntPtr query);

    [LibraryImport(CoreFoundationPath)]
    private static partial void CFRelease(IntPtr cf);

    [LibraryImport(CoreFoundationPath, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string text, uint encoding);

    [LibraryImport(CoreFoundationPath)]
    private static partial IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

    [LibraryImport(CoreFoundationPath)]
    private static partial nint CFDataGetLength(IntPtr data);

    [LibraryImport(CoreFoundationPath)]
    private static partial IntPtr CFDataGetBytePtr(IntPtr data);

    [LibraryImport(CoreFoundationPath)]
    private static partial IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count, IntPtr keyCallBacks, IntPtr valueCallBacks);

    /// <summary>Owns the CoreFoundation objects created for one call and releases them together.</summary>
    private sealed class CfScope : IDisposable
    {
        private readonly List<IntPtr> _owned = [];

        public IntPtr String(string text) => Own(CFStringCreateWithCString(IntPtr.Zero, text, KCFStringEncodingUTF8));

        public IntPtr Data(byte[] bytes) => Own(CFDataCreate(IntPtr.Zero, bytes, bytes.Length));

        public IntPtr Dictionary(params (IntPtr Key, IntPtr Value)[] pairs)
        {
            var k = Keys.Value;
            return Own(CFDictionaryCreate(
                IntPtr.Zero,
                pairs.Select(p => p.Key).ToArray(),
                pairs.Select(p => p.Value).ToArray(),
                pairs.Length,
                k.KeyCallBacks,
                k.ValueCallBacks));
        }

        public void Dispose()
        {
            foreach (var cf in _owned)
            {
                CFRelease(cf);
            }
        }

        private IntPtr Own(IntPtr cf)
        {
            if (cf == IntPtr.Zero)
            {
                throw new CredentialUnavailableException("CoreFoundation allocation failed.");
            }

            _owned.Add(cf);
            return cf;
        }
    }

    /// <summary>The framework's exported CFStringRef constants, read once by symbol.</summary>
    private sealed record Constants(
        IntPtr Class,
        IntPtr ClassGenericPassword,
        IntPtr AttrService,
        IntPtr AttrAccount,
        IntPtr AttrLabel,
        IntPtr ValueData,
        IntPtr ReturnData,
        IntPtr MatchLimit,
        IntPtr MatchLimitOne,
        IntPtr True,
        IntPtr KeyCallBacks,
        IntPtr ValueCallBacks)
    {
        public static Constants Load()
        {
            var security = NativeLibrary.Load(SecurityPath);
            var cf = NativeLibrary.Load(CoreFoundationPath);
            IntPtr Value(IntPtr library, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));
            return new Constants(
                Value(security, "kSecClass"),
                Value(security, "kSecClassGenericPassword"),
                Value(security, "kSecAttrService"),
                Value(security, "kSecAttrAccount"),
                Value(security, "kSecAttrLabel"),
                Value(security, "kSecValueData"),
                Value(security, "kSecReturnData"),
                Value(security, "kSecMatchLimit"),
                Value(security, "kSecMatchLimitOne"),
                Value(cf, "kCFBooleanTrue"),
                NativeLibrary.GetExport(cf, "kCFTypeDictionaryKeyCallBacks"),
                NativeLibrary.GetExport(cf, "kCFTypeDictionaryValueCallBacks"));
        }
    }
}
