using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Mindweave.Application.Credentials;
using Mindweave.Application.Export;
using Mindweave.Application.Languages;
using Mindweave.Application.Preferences;
using Mindweave.Application.Startup;
using Mindweave.Application.Storage;
using Mindweave.Application.Tools.Mcp;

namespace Mindweave.Application.Diagnostics;

/// <summary>What the user chose to leave in the package (D2 §2.2): both on by default.</summary>
public sealed record DiagnosticChoice(bool ModelsAndHosts = true, bool Logs = true);

/// <summary>One plain file of the package, by its path inside the zip.</summary>
public sealed record DiagnosticFile(string Path, string Text);

/// <summary>Counts of what the database holds; no names, no content.</summary>
public sealed record DataCounts(
    long Workspaces,
    long Conversations,
    long Messages,
    long SummaryNodes,
    long SummaryVersions,
    IReadOnlyList<(string Origin, long Count)> Resources);

/// <summary>A size on disk, named by what it is, never by its path.</summary>
public sealed record SizeOnDisk(string What, long Bytes);

/// <summary>One log file as far as it could be read.</summary>
public sealed record LogFile(string Name, string Text);

/// <summary>Facts the package reads from the machine (Infrastructure); none of them opens what the user wrote.</summary>
public interface IDiagnosticFacts
{
    /// <summary>Counts from the open database. Throws when it is not open.</summary>
    Task<DataCounts> CountAsync(CancellationToken cancellationToken);

    /// <summary>The database file and the data folders' sizes; a part that cannot be measured is left out.</summary>
    IReadOnlyList<SizeOnDisk> Sizes();

    /// <summary>The log files, oldest first, each read as far as it goes (one in use is still read).</summary>
    IReadOnlyList<LogFile> ReadLogs();
}

/// <summary>Writes the package as one zip through the atomic file write (Infrastructure).</summary>
public interface IDiagnosticPackageWriter
{
    /// <exception cref="ExportWriteException">The file could not be written; no partial file is left where it can be.</exception>
    Task<ExportWriteMode> WriteAsync(ExportDestination destination, IReadOnlyList<DiagnosticFile> files, CancellationToken cancellationToken);
}

/// <summary>What the export dialog lists before anything is written.</summary>
public sealed record DiagnosticPreview(int Requests, int LogFiles, long LogBytes, bool DataAvailable);

/// <summary>
/// The diagnostic package (D2 §2.2, requirements §10.8, architecture §17.2): a zip of plain files the user saves and sends by
/// their own means. It is built from a list of allowed fields, never by serializing storage records (the E5 rule); the log is
/// its only free text, scrubbed again with every stored key and with the home folder written as <c>~</c>. Without the
/// database (the recovery page) it holds the summary and the logs only.
/// </summary>
public sealed partial class DiagnosticPackageService(
    AppEnvironment environment,
    IDiagnosticFacts facts,
    IRequestList requests,
    IBackupFiles backups,
    IStorageStatus storage,
    IModelSettingsStore models,
    IToolSettings tools,
    IWebSearchSettings webSearch,
    IMcpServers mcp,
    StoredKeys keys,
    IClock clock,
    IDiagnosticPackageWriter writer,
    ILogger<DiagnosticPackageService> logger)
{
    /// <summary>How far back the request records go, as the log does.</summary>
    public const int DaysKept = 14;

    /// <summary>More requests than this in two weeks are cut, newest kept.</summary>
    public const int MaxRequests = 50_000;

    /// <summary>What would leave: the number of request records and of log files, for the dialog.</summary>
    public async Task<DiagnosticPreview> PreviewAsync(bool dataAvailable, CancellationToken cancellationToken)
    {
        var count = 0;
        if (dataAvailable)
        {
            count = (await requests.RecentAsync(clock.UtcNow.AddDays(-DaysKept), failuresOnly: false, MaxRequests, cancellationToken).ConfigureAwait(false)).Count;
        }

        var logs = facts.ReadLogs();
        return new DiagnosticPreview(count, logs.Count, logs.Sum(l => (long)Encoding.UTF8.GetByteCount(l.Text)), dataAvailable);
    }

    /// <summary>The files of the package; <paramref name="dataAvailable"/> false builds the recovery page's (summary and logs).</summary>
    public async Task<IReadOnlyList<DiagnosticFile>> BuildAsync(DiagnosticChoice choice, bool dataAvailable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(choice);
        var names = new Pseudonyms(choice.ModelsAndHosts);
        var scrub = await keys.ReadAllAsync(dataAvailable, cancellationToken).ConfigureAwait(false);
        var files = new List<DiagnosticFile>();
        DataCounts? counts = null;
        string? settings = null;
        string? rows = null;
        if (dataAvailable)
        {
            try
            {
                counts = await facts.CountAsync(cancellationToken).ConfigureAwait(false);
                var recent = await requests.RecentAsync(clock.UtcNow.AddDays(-DaysKept), failuresOnly: false, MaxRequests, cancellationToken).ConfigureAwait(false);
                rows = RequestsCsv(recent, names, scrub.Keys);
                settings = await SettingsAsync(names, cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Whatever kept the data from being read, the package is still made with what does not need it.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogDataUnavailable(logger, ex);
                dataAvailable = false;
            }
        }

        files.Add(new DiagnosticFile("summary.txt", Summary(counts, dataAvailable, scrub)));
        if (rows is not null)
        {
            files.Add(new DiagnosticFile("requests.csv", rows));
        }

        if (settings is not null)
        {
            files.Add(new DiagnosticFile("settings.txt", settings));
        }

        if (choice.Logs)
        {
            foreach (var log in facts.ReadLogs())
            {
                files.Add(new DiagnosticFile("logs/" + log.Name, ScrubLog(log.Text, scrub.Keys)));
            }
        }

        return files;
    }

    /// <summary>Builds the package and writes it to <paramref name="destination"/>.</summary>
    public async Task<ExportWriteMode> SaveAsync(ExportDestination destination, DiagnosticChoice choice, bool dataAvailable, CancellationToken cancellationToken)
    {
        var files = await BuildAsync(choice, dataAvailable, cancellationToken).ConfigureAwait(false);
        var mode = await writer.WriteAsync(destination, files, cancellationToken).ConfigureAwait(false);
        var count = files.Count;
        LogSaved(logger, count, choice.ModelsAndHosts, choice.Logs);
        return mode;
    }

    /// <summary>A log line as it leaves: every stored key and credential shape removed, the home folder as <c>~</c>.</summary>
    internal static string ScrubLog(string text, IReadOnlyList<string> knownKeys)
    {
        var scrubbed = Redaction.Scrub(text, knownKeys);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 1 ? scrubbed.Replace(home, "~", StringComparison.Ordinal) : scrubbed;
    }

    private string Summary(DataCounts? counts, bool dataAvailable, StoredKeys.Result scrub)
    {
        var text = new StringBuilder();
        Line("Mindweave diagnostic summary");
        Line($"Made: {Iso(clock.UtcNow)}");
        Line($"App: {Installation.App(environment)}{(environment.CommitDate is { } date ? $", commit of {date}" : string.Empty)}");
        Line($"Channel: {environment.Channel}");
        Line($"System: {Installation.System()}");
        Line($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Line($"Interface language: {CurrentLanguage.Value}");
        Line($"Schema version of this build: {environment.SchemaVersion}");
        Line($"Storage: {(!dataAvailable ? "the data could not be opened" : storage.IsBlocked ? "paused after a failed write" : "takes writes")}");
        Line($"Logs scrubbed with: {(scrub.StoreAnswered ? $"{scrub.Keys.Count} stored keys and credential shapes" : "credential shapes only; the system's key store did not answer")}");
        Line(string.Empty);
        Line("Sizes");
        foreach (var size in facts.Sizes())
        {
            Line($"  {size.What}: {size.Bytes.ToString("N0", CultureInfo.InvariantCulture)} bytes");
        }

        if (counts is not null)
        {
            Line(string.Empty);
            Line("Counts");
            Line($"  workspaces: {counts.Workspaces}");
            Line($"  conversations: {counts.Conversations}");
            Line($"  messages: {counts.Messages}");
            Line($"  summary nodes: {counts.SummaryNodes}");
            Line($"  summary versions: {counts.SummaryVersions}");
            foreach (var (origin, count) in counts.Resources)
            {
                Line($"  resources from {origin}: {count}");
            }
        }

        Line(string.Empty);
        Line("Backups");
        IReadOnlyList<BackupInfo> list;
        IReadOnlyList<LeftoverDatabase> leftovers;
        try
        {
            list = backups.ListBackups();
            leftovers = backups.ListLeftovers();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogBackupsUnreadable(logger, ex);
            list = [];
            leftovers = [];
        }

        foreach (var backup in list.OrderBy(b => b.CreatedAt))
        {
            Line($"  {Iso(backup.CreatedAt)}  {backup.Reason}  schema {backup.SchemaVersion}  {backup.SizeBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes");
        }

        if (list.Count == 0)
        {
            Line("  none");
        }

        Line($"Replaced databases kept: {leftovers.Count}");
        return text.ToString();

        void Line(string line) => text.Append(line).Append('\n');
    }

    private async Task<string> SettingsAsync(Pseudonyms names, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var model = await models.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        Line("Model connection");
        if (model is null)
        {
            Line("  not configured");
        }
        else
        {
            var c = model.Capabilities;
            Line($"  protocol: {model.Protocol}");
            Line($"  model: {names.Model(model.Model)}");
            Line($"  service host: {names.Host(RequestRow.HostOf(model.BaseUrl))}");
            Line($"  key: {(model.CredentialRef.Length > 0 ? "set" : "not set")}");
            Line($"  output limit: {(model.MaxOutputTokens is { } limit ? limit.ToString(CultureInfo.InvariantCulture) : "not set")}");
            Line($"  context window: {(c.ContextWindowTokens is { } window ? window.ToString(CultureInfo.InvariantCulture) : "default")}");
            Line($"  capabilities: streaming {YesNo(c.Streaming)}, tool calls {YesNo(c.ToolCalls)}, image input {YesNo(c.ImageInput)}, reasoning output {YesNo(c.ReasoningOutput)}, prompt caching {YesNo(c.PromptCaching)}");
        }

        Line(string.Empty);
        Line("Tools");
        Line($"  use tools: {(await tools.GetEnabledAsync(cancellationToken).ConfigureAwait(false) ? "on" : "off")}");
        var limits = await tools.GetLimitsAsync(cancellationToken).ConfigureAwait(false);
        Line(string.Create(CultureInfo.InvariantCulture, $"  run limits: {limits.ModelRequests} model requests, {limits.ToolCalls} tool calls, {limits.Duration.TotalMinutes:0} min, {limits.Tokens} tokens"));
        var web = await webSearch.GetAsync(cancellationToken).ConfigureAwait(false);
        Line($"  web search: {web.ProviderId}, key {(web.HasKey ? "set" : "not set")}, default {web.DefaultMode}");
        var servers = await mcp.ListAsync(cancellationToken).ConfigureAwait(false);
        Line(string.Create(
            CultureInfo.InvariantCulture,
            $"  MCP servers: {servers.Count} ({servers.Count(s => s.Kind == McpServerKind.Command)} command, {servers.Count(s => s.Kind == McpServerKind.Http)} HTTP), {servers.Sum(s => s.Tools.Count)} tools enabled"));
        return text.ToString();

        void Line(string line) => text.Append(line).Append('\n');
    }

    /// <summary>RFC 4180 with CRLF line ends on every system, so the file reads the same wherever it is opened.</summary>
    private static string RequestsCsv(IReadOnlyList<RequestRow> rows, Pseudonyms names, IReadOnlyList<string> knownKeys)
    {
        var text = new StringBuilder();
        Row("started_at", "purpose", "model", "service_host", "outcome", "http_status", "request_id", "error_kind", "error_text", "duration_ms", "response_ms", "first_text_ms", "prompt_tokens", "completion_tokens", "cache_read_tokens", "cache_write_tokens");
        foreach (var r in rows)
        {
            Row(
                Iso(r.StartedAt),
                r.Purpose,
                names.Model(r.Model),
                names.Host(r.Host),
                r.Outcome.ToString(),
                Number(r.HttpStatus),
                r.ProviderRequestId,
                r.ErrorKind?.ToString(),
                r.ErrorDetail is { Length: > 0 } detail ? Redaction.Scrub(detail, knownKeys) : null,
                Number((long)r.Duration.TotalMilliseconds),
                Number((long?)r.TimeToResponse?.TotalMilliseconds),
                Number((long?)r.TimeToFirstText?.TotalMilliseconds),
                Number(r.PromptTokens),
                Number(r.CompletionTokens),
                Number(r.CacheReadTokens),
                Number(r.CacheWriteTokens));
        }

        return text.ToString();

        void Row(params string?[] fields)
        {
            text.AppendJoin(',', fields.Select(Field)).Append("\r\n");
        }

        static string Field(string? value) =>
            value is null ? string.Empty
            : value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;

        static string? Number(long? value) => value?.ToString(CultureInfo.InvariantCulture);
    }

    private static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string YesNo(bool value) => value ? "yes" : "no";

    [LoggerMessage(Level = LogLevel.Warning, Message = "The diagnostic package was made without the data; reading it failed")]
    private static partial void LogDataUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The diagnostic package lists no backups; the folder could not be read")]
    private static partial void LogBackupsUnreadable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Diagnostic package saved: {Files} files, model names and hosts {ModelsAndHosts}, logs {Logs}")]
    private static partial void LogSaved(ILogger logger, int files, bool modelsAndHosts, bool logs);

    /// <summary>Model names and hosts as they are, or as "model 1", "host 1" (the same name the same number) when left out.</summary>
    private sealed class Pseudonyms(bool keep)
    {
        private readonly Dictionary<string, string> _models = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _hosts = new(StringComparer.OrdinalIgnoreCase);

        public string? Model(string? name) => Name(name, _models, "model");

        public string? Host(string? name) => Name(name, _hosts, "host");

        private string? Name(string? name, Dictionary<string, string> seen, string kind)
        {
            if (keep || name is null)
            {
                return name;
            }

            if (!seen.TryGetValue(name, out var alias))
            {
                alias = string.Create(CultureInfo.InvariantCulture, $"{kind} {seen.Count + 1}");
                seen[name] = alias;
            }

            return alias;
        }
    }
}
