using System.Text.Json;
using System.Text.Json.Serialization;
using Mindweave.Application.Credentials;
using Mindweave.Application.Storage;
using Mindweave.Core.Runs;

namespace Mindweave.Application.Tools.Mcp;

/// <summary>The secret values of a server (decision M3): its environment's values and its header's value.</summary>
public sealed record McpServerSecrets(IReadOnlyDictionary<string, string> Environment, string? HeaderValue)
{
    public static McpServerSecrets None { get; } = new(new Dictionary<string, string>(), null);

    /// <summary>The values long enough to be worth removing from text (log lines, errors).</summary>
    public IEnumerable<string> Values => Environment.Values.Append(HeaderValue ?? string.Empty).Where(v => v.Length >= 8);
}

/// <summary>
/// 设置 › 工具 › MCP 服务器 (requirements §10.4.6): the servers the user added and the tools they turned on. Kept in the
/// preference store as one JSON value (<c>mcp.servers</c>, decision M2); secrets in the credential store (decision M3). Only
/// the user adds or removes servers (architecture §15.8): nothing from a conversation reaches these methods.
/// </summary>
public interface IMcpServers
{
    Task<IReadOnlyList<McpServerConfig>> ListAsync(CancellationToken cancellationToken);

    Task<McpServerConfig?> GetAsync(string id, CancellationToken cancellationToken);

    /// <summary>What the confirmation shows, or why the draft cannot be added.</summary>
    McpAddPreview Preview(McpServerDraft draft);

    /// <summary>Saves a server after the user confirmed <see cref="Preview"/>; it is not started. Throws <see cref="ArgumentException"/> for a draft that cannot be added.</summary>
    Task<McpServerConfig> AddAsync(McpServerDraft draft, CancellationToken cancellationToken);

    /// <summary>Removes a server, its secrets and its working folder (decision M4). The manager stops it first.</summary>
    Task RemoveAsync(string id, CancellationToken cancellationToken);

    /// <summary>The secret values for a connection. Throws <see cref="CredentialUnavailableException"/>.</summary>
    Task<McpServerSecrets> ReadSecretsAsync(McpServerConfig server, CancellationToken cancellationToken);

    /// <summary>Turns a tool on with its current definition (decision M5); the name the model gets is fixed now (decision M9).</summary>
    Task<McpToolSetting> EnableToolAsync(string serverId, McpToolInfo tool, CancellationToken cancellationToken);

    /// <summary>Turns a tool off (its setting is forgotten).</summary>
    Task DisableToolAsync(string serverId, string tool, CancellationToken cancellationToken);

    /// <summary>The user's choice of risk for an enabled tool; null goes back to the one its definition gave.</summary>
    Task SetRiskAsync(string serverId, string tool, ToolRisk? risk, CancellationToken cancellationToken);

    /// <summary>
    /// Compares what the server lists now with the enabled tools' definitions (decision M6): a tool whose definition changed,
    /// or that is gone, is turned off with the reason. Returns the tools turned off now.
    /// </summary>
    Task<IReadOnlyList<string>> ReconcileAsync(string serverId, IReadOnlyList<McpToolInfo> listed, CancellationToken cancellationToken);

    /// <summary>A server was added or removed, or a tool's setting changed.</summary>
    event EventHandler? Changed;
}

public sealed class McpServerSettings(IPreferenceStore store, ICredentialStore credentials, IMcpConnector connector) : IMcpServers, IDisposable
{
    public const string Key = "mcp.servers";

    public const string ChangedDefinition = "工具的定义已变化（说明、输入格式或只读声明），请重新查看后再启用。";

    public const string Vanished = "服务器已不再提供这个工具（可能改了名字），请重新查看后再启用。";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public event EventHandler? Changed;

    public void Dispose() => _gate.Dispose();

    public async Task<IReadOnlyList<McpServerConfig>> ListAsync(CancellationToken cancellationToken) =>
        Parse(await store.GetAsync(Key, cancellationToken).ConfigureAwait(false));

    public async Task<McpServerConfig?> GetAsync(string id, CancellationToken cancellationToken) =>
        (await ListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(s => s.Id == id);

    public McpAddPreview Preview(McpServerDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var env = draft.Environment ?? new Dictionary<string, string>();
        // Each argument without the spaces around it (a trailing space pasted into a line is never meant); blank ones dropped.
        IReadOnlyList<string> arguments = [.. (draft.Arguments ?? []).Select(a => a.Trim()).Where(a => a.Length > 0)];
        string? problem = null;
        if (string.IsNullOrWhiteSpace(draft.Name))
        {
            problem = "请填写服务器的名称。";
        }

        if (draft.Kind == McpServerKind.Command)
        {
            if (problem is null && string.IsNullOrWhiteSpace(draft.Command))
            {
                problem = "请填写要运行的命令。";
            }

            var bad = env.Keys.FirstOrDefault(k => !McpNames.IsEnvironmentName(k));
            if (problem is null && bad is not null)
            {
                problem = $"环境变量名“{bad}”不合法：只能用字母、数字与下划线，不能以数字开头。";
            }

            return new McpAddPreview(problem, draft.Command?.Trim(), arguments, connector.WorkingDirectoryOf(draft.Id), [.. env.Keys.Order(StringComparer.Ordinal)], null, null);
        }

        Uri? url = null;
        if (problem is null && (!Uri.TryCreate(draft.Url?.Trim(), UriKind.Absolute, out url) || url.Scheme is not ("http" or "https")))
        {
            problem = "请填写以 http:// 或 https:// 开头的完整地址。";
        }
        else if (problem is null && url!.UserInfo.Length > 0)
        {
            problem = "地址里不能带用户名或密码；需要密钥时请填写请求头。";
        }

        var header = string.IsNullOrWhiteSpace(draft.HeaderName) ? null : draft.HeaderName.Trim();
        if (problem is null && header is not null && !McpNames.IsHeaderName(header))
        {
            problem = $"请求头名称“{header}”不合法。";
        }

        if (problem is null && header is not null && string.IsNullOrEmpty(draft.HeaderValue))
        {
            problem = "请填写请求头的值。";
        }

        if (problem is null && header is null && !string.IsNullOrEmpty(draft.HeaderValue))
        {
            problem = "请填写请求头的名称。";
        }

        return new McpAddPreview(problem, null, [], null, [], problem is null ? url : null, header);
    }

    public async Task<McpServerConfig> AddAsync(McpServerDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var preview = Preview(draft);
        if (!preview.CanAdd)
        {
            throw new ArgumentException(preview.Problem, nameof(draft));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        McpServerConfig server;
        try
        {
            var all = Parse(await store.GetAsync(Key, cancellationToken).ConfigureAwait(false)).ToList();
            var env = draft.Kind == McpServerKind.Command ? draft.Environment ?? new Dictionary<string, string>() : new Dictionary<string, string>();
            var headerValue = draft.Kind == McpServerKind.Http ? draft.HeaderValue : null;
            string? reference = null;
            if (env.Count > 0 || !string.IsNullOrEmpty(headerValue))
            {
                reference = $"mcp/{draft.Id}/{Guid.CreateVersion7():N}";
                var secret = JsonSerializer.Serialize(new SecretsDto { Env = new Dictionary<string, string>(env), Header = headerValue }, Json);
                await credentials.WriteAsync(reference, secret, cancellationToken).ConfigureAwait(false);
            }

            server = new McpServerConfig(
                draft.Id,
                draft.Name.Trim(),
                McpNames.Slug(draft.Name, all.Select(s => s.Slug)),
                draft.Kind,
                preview.Command,
                draft.Kind == McpServerKind.Command ? [.. preview.Arguments] : [],
                preview.EnvironmentNames,
                preview.Url,
                preview.HeaderName,
                reference,
                []);
            all.Add(server);
            await SaveAsync(all, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return server;
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        McpServerConfig? removed;
        try
        {
            var all = Parse(await store.GetAsync(Key, cancellationToken).ConfigureAwait(false)).ToList();
            removed = all.FirstOrDefault(s => s.Id == id);
            if (removed is null)
            {
                return;
            }

            all.Remove(removed);
            await SaveAsync(all, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (removed.Credential is { } reference)
        {
            try
            {
                await credentials.DeleteAsync(reference, CancellationToken.None).ConfigureAwait(false);
            }
            catch (CredentialUnavailableException)
            {
                // A locked store keeps it; it is unreachable without the setting either way.
            }
        }

        connector.DeleteWorkingDirectory(id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<McpServerSecrets> ReadSecretsAsync(McpServerConfig server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (server.Credential is not { } reference)
        {
            return McpServerSecrets.None;
        }

        var text = await credentials.ReadAsync(reference, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(text))
        {
            throw new CredentialUnavailableException("这个服务器的密钥在系统凭据服务中找不到了，请移除后重新添加。");
        }

        var dto = JsonSerializer.Deserialize<SecretsDto>(text, Json) ?? new SecretsDto();
        return new McpServerSecrets(dto.Env ?? [], dto.Header);
    }

    public Task<McpToolSetting> EnableToolAsync(string serverId, McpToolInfo tool, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return UpdateAsync(serverId, (all, server) =>
        {
            var taken = all.Where(s => s.Id != serverId).SelectMany(s => s.Tools)
                .Concat(server.Tools.Where(t => t.Name != tool.Name))
                .Select(t => t.ModelName)
                .Concat(BuiltInNames)
                .ToList();
            var existing = server.Tool(tool.Name);
            var setting = new McpToolSetting(
                tool.Name,
                existing?.ModelName is { } kept && !taken.Contains(kept, StringComparer.Ordinal) ? kept : McpNames.ToolName(server.Slug, tool.Name, taken),
                tool,
                tool.Hash(),
                tool.DefaultRisk,
                existing?.ChosenRisk);
            return (server with { Tools = [.. server.Tools.Where(t => t.Name != tool.Name), setting] }, setting);
        }, cancellationToken);
    }

    public Task DisableToolAsync(string serverId, string tool, CancellationToken cancellationToken) =>
        UpdateAsync(serverId, (_, server) => (server with { Tools = [.. server.Tools.Where(t => t.Name != tool)] }, true), cancellationToken);

    public Task SetRiskAsync(string serverId, string tool, ToolRisk? risk, CancellationToken cancellationToken) =>
        UpdateAsync(serverId, (_, server) =>
        {
            var setting = server.Tool(tool) ?? throw new InvalidOperationException($"Tool {tool} is not enabled.");
            return (server with { Tools = [.. server.Tools.Select(t => t.Name == tool ? t with { ChosenRisk = risk == t.DefaultRisk ? null : risk } : t)] }, true);
        }, cancellationToken);

    public async Task<IReadOnlyList<string>> ReconcileAsync(string serverId, IReadOnlyList<McpToolInfo> listed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(listed);
        var byName = listed.GroupBy(t => t.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var changed = new List<string>();
        await UpdateAsync(serverId, (_, server) =>
        {
            var tools = server.Tools.Select(t =>
            {
                if (!t.Enabled)
                {
                    return t;
                }

                var reason = !byName.TryGetValue(t.Name, out var now) ? Vanished
                    : now.Hash() != t.Hash ? ChangedDefinition
                    : null;
                if (reason is null)
                {
                    return t;
                }

                changed.Add(t.Name);
                return t with { Enabled = false, DisabledReason = reason };
            }).ToList();
            return (changed.Count == 0 ? server : server with { Tools = tools }, true);
        }, cancellationToken, notify: false).ConfigureAwait(false);
        if (changed.Count > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    /// <summary>Names the built-in tools use; a third-party tool never gets one of them.</summary>
    private static readonly string[] BuiltInNames = [SearchAttachmentsTool.ToolName, SearchSummariesTool.ToolName, WebSearchTool.ToolName, FetchPageTool.ToolName];

    private async Task<T> UpdateAsync<T>(string serverId, Func<IReadOnlyList<McpServerConfig>, McpServerConfig, (McpServerConfig Server, T Result)> change, CancellationToken cancellationToken, bool notify = true)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        T result;
        try
        {
            var all = Parse(await store.GetAsync(Key, cancellationToken).ConfigureAwait(false)).ToList();
            var index = all.FindIndex(s => s.Id == serverId);
            if (index < 0)
            {
                throw new InvalidOperationException("这个服务器已被移除。");
            }

            (var updated, result) = change(all, all[index]);
            if (!ReferenceEquals(updated, all[index]))
            {
                all[index] = updated;
                await SaveAsync(all, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (notify)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    private Task SaveAsync(IReadOnlyList<McpServerConfig> servers, CancellationToken cancellationToken) =>
        store.SetAsync(Key, JsonSerializer.Serialize(servers.Select(ServerDto.Of).ToList(), Json), cancellationToken);

    /// <summary>The stored list; an unreadable value is an empty list (nothing is offered from it).</summary>
    public static IReadOnlyList<McpServerConfig> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return [];
        }

        try
        {
            return [.. (JsonSerializer.Deserialize<List<ServerDto>>(stored, Json) ?? []).Select(d => d.ToConfig())];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed class SecretsDto
    {
        public Dictionary<string, string>? Env { get; set; }

        public string? Header { get; set; }
    }

    private sealed class ServerDto
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Slug { get; set; } = string.Empty;

        public McpServerKind Kind { get; set; }

        public string? Command { get; set; }

        public List<string>? Arguments { get; set; }

        public List<string>? Environment { get; set; }

        public string? Url { get; set; }

        public string? Header { get; set; }

        public string? Credential { get; set; }

        public List<ToolDto>? Tools { get; set; }

        public static ServerDto Of(McpServerConfig s) => new()
        {
            Id = s.Id,
            Name = s.Name,
            Slug = s.Slug,
            Kind = s.Kind,
            Command = s.Command,
            Arguments = [.. s.Arguments],
            Environment = [.. s.EnvironmentNames],
            Url = s.Url?.OriginalString,
            Header = s.HeaderName,
            Credential = s.Credential,
            Tools = [.. s.Tools.Select(ToolDto.Of)],
        };

        public McpServerConfig ToConfig() => new(
            Id,
            Name,
            Slug,
            Kind,
            Command,
            Arguments ?? [],
            Environment ?? [],
            Url is null ? null : new Uri(Url, UriKind.Absolute),
            Header,
            Credential,
            [.. (Tools ?? []).Select(t => t.ToSetting())]);
    }

    private sealed class ToolDto
    {
        public string Name { get; set; } = string.Empty;

        public string ModelName { get; set; } = string.Empty;

        public string? Title { get; set; }

        public string? Description { get; set; }

        public string Schema { get; set; } = "{}";

        public bool? ReadOnly { get; set; }

        public bool? Destructive { get; set; }

        public bool? Idempotent { get; set; }

        public bool? OpenWorld { get; set; }

        public string Hash { get; set; } = string.Empty;

        public ToolRisk DefaultRisk { get; set; } = ToolRisk.SideEffect;

        public ToolRisk? Risk { get; set; }

        public bool Enabled { get; set; } = true;

        public string? DisabledReason { get; set; }

        public static ToolDto Of(McpToolSetting t) => new()
        {
            Name = t.Name,
            ModelName = t.ModelName,
            Title = t.Definition.Title,
            Description = t.Definition.Description,
            Schema = t.Definition.InputSchema,
            ReadOnly = t.Definition.ReadOnlyHint,
            Destructive = t.Definition.DestructiveHint,
            Idempotent = t.Definition.IdempotentHint,
            OpenWorld = t.Definition.OpenWorldHint,
            Hash = t.Hash,
            DefaultRisk = t.DefaultRisk,
            Risk = t.ChosenRisk,
            Enabled = t.Enabled,
            DisabledReason = t.DisabledReason,
        };

        public McpToolSetting ToSetting() => new(
            Name,
            ModelName,
            new McpToolInfo(Name, Title, Description, Schema, ReadOnly, Destructive, Idempotent, OpenWorld),
            Hash,
            DefaultRisk,
            Risk,
            Enabled,
            DisabledReason);
    }
}
