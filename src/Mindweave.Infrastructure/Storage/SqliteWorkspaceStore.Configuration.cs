using System.Text.Json;
using Mindweave.Core.Chat;
using Mindweave.Application.Chat;

namespace Mindweave.Infrastructure.Storage;

public sealed partial class SqliteWorkspaceStore
{
    public Task<AiConfiguration?> LoadActiveConfigurationAsync() => ReadAsync(connection =>
    {
        using var command = ChatCommand(connection, null, "SELECT configuration_json FROM active_ai_configuration WHERE singleton=1");
        var configuration = command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<AiConfiguration>(json) : null;
        if (configuration is not null) SummaryThinkingPolicy.ValidateConfiguration(configuration);
        return configuration;
    });

    public Task SaveConfigurationAsync(AiConfiguration configuration)
    {
        ValidateConfiguration(configuration);
        return WriteAsync((connection, transaction) =>
        {
            using var command = ChatCommand(connection, transaction, """
                INSERT INTO active_ai_configuration VALUES(1,$json)
                ON CONFLICT(singleton) DO UPDATE SET configuration_json=$json
                """, ("$json", JsonSerializer.Serialize(configuration)));
            command.ExecuteNonQuery();
            return true;
        });
    }

    private static void ValidateConfiguration(AiConfiguration configuration)
    {
        SummaryThinkingPolicy.ValidateConfiguration(configuration);
        if (configuration.ConnectionId == Guid.Empty || configuration.ModelConfigId == Guid.Empty ||
            string.IsNullOrWhiteSpace(configuration.Name) || string.IsNullOrWhiteSpace(configuration.CredentialReference) ||
            string.IsNullOrWhiteSpace(configuration.ModelId) || configuration.ContextTokenBudget < 1 ||
            configuration.MaxOutputTokens < 1 || configuration.MaxOutputTokens >= configuration.ContextTokenBudget ||
            configuration.TokenLimitParameter is not ("max_tokens" or "max_completion_tokens") ||
            !Uri.TryCreate(configuration.BaseUrl, UriKind.Absolute, out var uri) ||
            !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("AI 配置无效，请检查连接、模型、凭据引用与预算。", nameof(configuration));
    }
}
