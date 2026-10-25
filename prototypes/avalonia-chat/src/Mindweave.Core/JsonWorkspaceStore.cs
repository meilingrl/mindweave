using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mindweave.Core;

public sealed class JsonWorkspaceStore(string filePath)
{
    private sealed record Snapshot(int Version, string CurrentConversationId, List<ConversationData> Conversations);
    private sealed record ConversationData(string Id, string Title, SourceAnchor? Source, List<MessageData> Messages, ReadingPosition Position, string Draft);
    private sealed record MessageData(string Id, bool IsUser, string Markdown, ReplyStatus Status);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    public string FilePath { get; } = Path.GetFullPath(filePath);

    public async Task SaveAsync(ExplorationState state)
    {
        // Copy on the caller's state thread; asynchronous IO must not enumerate mutable messages later.
        var snapshot = new Snapshot(1, state.Current.Id, state.Conversations.Select(c => new ConversationData(c.Id, c.Title, c.Source,
            c.Messages.Select(m => new MessageData(m.Id, m.IsUser, m.Markdown, m.Status)).ToList(), c.Position, c.Draft)).ToList());
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(async () =>
            {
                var directory = Path.GetDirectoryName(FilePath)!;
                Directory.CreateDirectory(directory);
                var temporary = Path.Combine(directory, "." + Path.GetFileName(FilePath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                    {
                        await JsonSerializer.SerializeAsync(output, snapshot, Options).ConfigureAwait(false);
                        await output.FlushAsync().ConfigureAwait(false);
                        output.Flush(flushToDisk: true);
                    }
                    File.Move(temporary, FilePath, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    public async Task<ExplorationState?> LoadAsync()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(FilePath).ConfigureAwait(false);
            var snapshot = JsonSerializer.Deserialize<Snapshot>(json, Options) ?? throw new InvalidDataException("保存文件为空。");
            if (snapshot.Version != 1 || snapshot.Conversations is not { Count: > 0 } || snapshot.Conversations.Any(x => x == null) ||
                snapshot.Conversations.Select(x => x.Id).Distinct().Count() != snapshot.Conversations.Count ||
                snapshot.Conversations.All(x => x.Id != snapshot.CurrentConversationId)) throw new InvalidDataException("保存文件的结构或版本无法识别。");
            var conversations = snapshot.Conversations.Select(data =>
            {
                if (string.IsNullOrWhiteSpace(data.Id) || data.Title == null || data.Messages == null || data.Position == null ||
                    data.Messages.Any(x => x == null || string.IsNullOrWhiteSpace(x.Id)) || !double.IsFinite(data.Position.Offset) ||
                    data.Position.CharacterIndex < 0 || data.Messages.Select(x => x.Id).Distinct().Count() != data.Messages.Count)
                    throw new InvalidDataException("会话记录不完整。");
                var conversation = new Conversation(data.Id, data.Title, data.Source) { Draft = data.Draft ?? "", Position = data.Position };
                conversation.Messages.AddRange(data.Messages.Select(m => new Message(m.Id, m.IsUser, m.Markdown ?? "", m.Status == ReplyStatus.Generating ? ReplyStatus.Interrupted : m.Status)));
                return conversation;
            }).ToList();
            foreach (var conversation in conversations)
            {
                var seen = new HashSet<string> { conversation.Id };
                var current = conversation;
                while (current.Source is { } source)
                {
                    var parent = conversations.SingleOrDefault(x => x.Id == source.ConversationId);
                    if (parent == null || !seen.Add(parent.Id))
                        throw new InvalidDataException("分支来源无法读取。");
                    var message = parent.Messages.SingleOrDefault(m => m.Id == source.MessageId && !m.IsUser);
                    var block = message == null ? null : MarkdownDocument.Parse(message.Markdown).SingleOrDefault(x => x.Id == source.BlockId);
                    if (block == null) throw new InvalidDataException("来源段落不存在。");
                    try
                    {
                        var mapped = MarkdownDocument.Anchor(parent.Id, source.MessageId, block, source.DisplayStart, source.DisplayEnd);
                        if (mapped.RawStart != source.RawStart || mapped.RawEnd != source.RawEnd || mapped.Quote != source.Quote)
                            throw new InvalidDataException("来源文字与保存的选区不一致。");
                    }
                    catch (ArgumentException exception) { throw new InvalidDataException("来源选区无效。", exception); }
                    current = parent;
                }
            }
            return new(conversations, snapshot.CurrentConversationId);
        }
        catch (JsonException exception) { throw new InvalidDataException("保存文件不是有效的 JSON，原文件已保留。", exception); }
    }
}
