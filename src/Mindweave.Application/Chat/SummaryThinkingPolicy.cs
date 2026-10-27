using Mindweave.Core.Chat;

namespace Mindweave.Application.Chat;

/// <summary>Validates only the opt-in summary policy; general connection rules remain with their existing owners.</summary>
public static class SummaryThinkingPolicy
{
    public static bool IsOfficialDeepSeekEndpoint(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || baseUrl != baseUrl.Trim() || baseUrl.Contains('\\') ||
            !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "api.deepseek.com", StringComparison.OrdinalIgnoreCase) || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;
        // Inspect the original path as well: Uri normalizes dot segments and escapes.
        // An alternate spelling must not silently turn into an allowed endpoint.
        var authorityStart = baseUrl.IndexOf("://", StringComparison.Ordinal);
        if (authorityStart < 0) return false;
        authorityStart += 3;
        var pathStart = baseUrl.IndexOf('/', authorityStart);
        var authority = pathStart < 0 ? baseUrl[authorityStart..] : baseUrl[authorityStart..pathStart];
        var path = pathStart < 0 ? "" : baseUrl[pathStart..];
        return !authority.Contains('@') && path is "" or "/" or "/v1" or "/v1/";
    }

    public static void ValidateConfiguration(AiConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!Enum.IsDefined(configuration.SummaryThinking))
            throw new ArgumentException("未知摘要思考策略，请选择服务默认或关闭摘要思考。", nameof(configuration));
        if (configuration.SummaryThinking == SummaryThinkingMode.Disabled && !IsOfficialDeepSeekEndpoint(configuration.BaseUrl))
            throw new ArgumentException("关闭摘要思考仅支持 DeepSeek 官方 HTTPS 地址 https://api.deepseek.com 或其 /v1 路径（默认 443 端口，不含用户信息、查询或片段）。", nameof(configuration));
    }
}
