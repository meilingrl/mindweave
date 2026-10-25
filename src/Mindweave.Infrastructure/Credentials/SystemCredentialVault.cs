using System.Security.Cryptography;
using System.Text;
using Mindweave.Application.Chat;

namespace Mindweave.Infrastructure.Credentials;

public sealed class CredentialVaultException(int statusCode, bool notFound = false) : Exception($"系统凭据不可用（状态 {statusCode}）；请检查系统凭据服务或授权。")
{
    public int StatusCode { get; } = statusCode;
    public bool NotFound { get; } = notFound;
}

public sealed class SystemCredentialVault : ICredentialVault
{
    private readonly string _namespace;
    private readonly string _prefix;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public SystemCredentialVault(string credentialNamespace)
    {
        if (string.IsNullOrWhiteSpace(credentialNamespace) || credentialNamespace.Length > 200 ||
            !credentialNamespace.StartsWith("Mindweave.", StringComparison.Ordinal) || credentialNamespace.Length <= 10 ||
            credentialNamespace.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-')) || credentialNamespace.Contains(".."))
            throw new ArgumentException("凭据命名空间必须属于 Mindweave 应用及轨道。", nameof(credentialNamespace));
        _namespace = credentialNamespace;
        _prefix = credentialNamespace + ":v1:";
    }

    public Task<string> WriteNewAsync(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) throw new ArgumentException("密钥不能为空。", nameof(secret));
        byte[] bytes;
        try { bytes = Utf8.GetBytes(secret); }
        catch (EncoderFallbackException) { throw new ArgumentException("密钥编码无效。", nameof(secret)); }
        if (bytes.Length > 2560) { CryptographicOperations.ZeroMemory(bytes); throw new ArgumentException("密钥超过系统凭据容量。", nameof(secret)); }
        var reference = _prefix + Guid.NewGuid().ToString("N");
        return Task.Run(() =>
        {
            try
            {
                if (OperatingSystem.IsMacOS()) MacCredentialStore.Write(_namespace, reference, bytes);
                else if (OperatingSystem.IsWindows()) WindowsCredentialStore.Write(reference, bytes);
                else throw Unsupported();
                return reference;
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        });
    }

    public Task<string> ReadAsync(string reference)
    {
        ValidateReference(reference);
        return Task.Run(() =>
        {
            var bytes = OperatingSystem.IsMacOS() ? MacCredentialStore.Read(_namespace, reference) :
                OperatingSystem.IsWindows() ? WindowsCredentialStore.Read(reference) : throw Unsupported();
            try { return Utf8.GetString(bytes); }
            catch (DecoderFallbackException) { throw new CredentialVaultException(-1); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        });
    }

    public Task DeleteAsync(string reference)
    {
        ValidateReference(reference);
        return Task.Run(() =>
        {
            if (OperatingSystem.IsMacOS()) MacCredentialStore.Delete(_namespace, reference);
            else if (OperatingSystem.IsWindows()) WindowsCredentialStore.Delete(reference);
            else throw Unsupported();
        });
    }
    private void ValidateReference(string reference)
    {
        if (reference is null || !reference.StartsWith(_prefix, StringComparison.Ordinal) || reference.Length != _prefix.Length + 32 ||
            !Guid.TryParseExact(reference[_prefix.Length..], "N", out var id) || id == Guid.Empty || reference[_prefix.Length..] != id.ToString("N"))
            throw new ArgumentException("凭据引用不属于当前应用轨道或版本无效。", nameof(reference));
    }
    private static PlatformNotSupportedException Unsupported() => new("当前平台不支持安全凭据存储；已保存聊天仍可读取。");
}
