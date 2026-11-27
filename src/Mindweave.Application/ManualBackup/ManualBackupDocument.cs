using Mindweave.Application.Exchange;
using Mindweave.Application.Import;
using Mindweave.Application.Storage;
using Mindweave.Application.SummaryLinks;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.ManualBackup;

/// <summary>
/// A conversation as it is stored, before anything is left out. Only <see cref="ManualBackupService"/> makes it and only
/// <see cref="ManualBackupDocument"/> reads it.
/// </summary>
/// <param name="Messages">In order, every attempt included.</param>
/// <param name="Models">The model's name of each answer.</param>
public sealed record ManualBackupConversation(
    ConversationSummary Row,
    IReadOnlyList<Message> Messages,
    BranchSource? Source,
    SummaryDetail? Summary,
    IReadOnlyDictionary<MessageId, RunRecord> Runs,
    IReadOnlyDictionary<MessageId, string> Models,
    ImportMark? Received);

/// <summary>A workspace as stored: its conversations, its summary links, and the material its messages refer to.</summary>
/// <param name="Texts">The extracted text of each resource that has one.</param>
public sealed record ManualBackupContents(
    Workspace Workspace,
    IReadOnlyList<ManualBackupConversation> Conversations,
    IReadOnlyList<SummaryLink> Links,
    IReadOnlyDictionary<ResourceId, StoredResource> Resources,
    IReadOnlyDictionary<ResourceId, StoredDerivative> Texts);

/// <summary>
/// The exchange document of kind <c>device</c> for one workspace (docs/design/sharing-backup.md §1). What a manual backup
/// holds is decided here and nowhere else: the package writer and the page see only this document.
/// </summary>
/// <remarks>
/// In the copy: the workspace's name; every conversation, archived ones included, with its stored title, whether the user
/// wrote it, its archived state and whether it was received, with the share's title; every finished question and answer
/// and every attempt, with their stored numbers and which answer replaced which; blocks in full; the material they refer to
/// with its extracted text; the steps of a run; branch sources as stored; every saved summary version, which is current,
/// the summary's name and read marks; summary links with their notes; the model's name on an answer and a version. Not in
/// it: keys, connections and their addresses, settings, drafts, reading positions, recommendations, link suggestions,
/// usage records, a summary's candidate and edit draft, exploration places, preferences, and an answer still being written.
/// </remarks>
public static class ManualBackupDocument
{
    public static ExchangeDocument Build(ManualBackupContents contents, DateTimeOffset madeAt, string appVersion)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var conversations = Rooted(contents.Conversations);
        var summaryOf = conversations.Where(c => c.Summary is not null).ToDictionary(c => c.Summary!.Node.Id, c => c.Row.Conversation.Id);
        var built = conversations.Select(Conversation).ToList();
        var referred = built.SelectMany(c => c.Content!.Messages).SelectMany(Referred).ToHashSet();
        return new ExchangeDocument(
            ExchangeDocument.DeviceKind,
            contents.Workspace.Name.Value,
            madeAt.ToUniversalTime(),
            appVersion,
            [.. built.OrderBy(c => c.StartedAt).ThenBy(c => c.Id.Value)],
            [.. contents.Links.Where(l => summaryOf.ContainsKey(l.NodeA) && summaryOf.ContainsKey(l.NodeB)).Select(l =>
                new ExchangeLink(summaryOf[l.NodeA], summaryOf[l.NodeB]) { Note = l.Note, Stored = new ExchangeStoredLink(l.Id, l.Origin, l.CreatedAt.ToUniversalTime()) })])
        {
            Workspace = new ExchangeWorkspace(contents.Workspace.Id, contents.Workspace.Name.Value, contents.Workspace.CreatedAt.ToUniversalTime()),
            Resources = [.. contents.Resources.Values.Where(r => referred.Contains(r.Resource.Id)).OrderBy(r => r.Resource.CreatedAt).ThenBy(r => r.Resource.Id.Value)
                .Select(r => Resource(r, contents.Texts.GetValueOrDefault(r.Resource.Id)))],
        };
    }

    /// <summary>The conversations whose ancestors are all there: one deleted while the workspace was read takes its branches with it.</summary>
    private static List<ManualBackupConversation> Rooted(IReadOnlyList<ManualBackupConversation> read)
    {
        var byId = read.ToDictionary(c => c.Row.Conversation.Id);
        bool IsThere(ManualBackupConversation c, int depth) =>
            c.Row.Parent is not { } parent || (depth < byId.Count && byId.TryGetValue(parent, out var up) && IsThere(up, depth + 1));
        return [.. read.Where(c => IsThere(c, 0))];
    }

    private static ExchangeConversation Conversation(ManualBackupConversation stored)
    {
        var conversation = stored.Row.Conversation;
        var messages = Finished(stored.Messages).Select(m => Message(m, stored)).ToList();
        var history = stored.Summary is { } summary ? History(summary) : null;
        var current = history?.Versions.Single(v => v.Id == history.Current);
        return new ExchangeConversation(
            conversation.Id,
            stored.Row.Parent,
            conversation.Title,
            conversation.CreatedAt.ToUniversalTime(),
            messages.Count(m => m.Role == MessageRole.User),
            history is not null)
        {
            TitleByUser = conversation.TitleSource == TitleSource.Manual,
            IsArchived = stored.Row.IsArchived,
            Received = stored.Received is { } mark ? new ExchangeReceived(mark.Title, mark.At.ToUniversalTime()) : null,
            Content = new ExchangeContent(
                messages,
                stored.Source is { } source ? Source(source) : null,
                current is null ? null : new ExchangeSummary(current.Id, current.Title, current.Body, current.At, current.ToSeq))
            {
                History = history,
            },
        };
    }

    /// <summary>Every finished message; while an answer is being written, up to the last finished answer, as a share has it (sharing.md §9).</summary>
    private static IEnumerable<Message> Finished(IReadOnlyList<Message> ordered)
    {
        var end = ConversationRules.HasUnfinishedAnswer(ordered)
            ? ordered.LastOrDefault(m => m.Role == MessageRole.Assistant && m.IsFinished)?.Seq ?? -1
            : int.MaxValue;
        return ordered.Where(m => m.Seq <= end && m.IsFinished);
    }

    private static ExchangeMessage Message(Message message, ManualBackupConversation stored) =>
        new(message.Id, message.Seq, message.Role, (message.Role == MessageRole.Assistant ? message.CompletedAt ?? message.CreatedAt : message.CreatedAt).ToUniversalTime(), message.State, message.Text)
        {
            Attachments = [.. ContentBlocks.AttachmentNamesOf(message.Blocks).Select(name => string.IsNullOrWhiteSpace(name) ? null : name)],
            Stored = new ExchangeStoredMessage(message.Blocks, message.CreatedAt.ToUniversalTime())
            {
                CompletedAt = message.CompletedAt?.ToUniversalTime(),
                Supersedes = message.Supersedes,
                Error = message.ErrorKind,
                Model = stored.Models.GetValueOrDefault(message.Id),
                Run = stored.Runs.TryGetValue(message.Id, out var run) ? Run(run) : null,
            },
        };

    private static ExchangeRun Run(RunRecord run) => new(
        [.. run.Steps.OrderBy(s => s.Ordinal).Select(s => new ExchangeRunStep(s.Ordinal, s.Kind, s.State, s.StartedAt.ToUniversalTime())
        {
            FinishedAt = s.FinishedAt?.ToUniversalTime(),
            CallId = s.CallId,
            Tool = s.Tool,
            Risk = s.Risk,
            Input = s.Input,
            Result = s.Result,
            Error = s.ErrorKind,
            Approval = s.Approval,
        })],
        run.LimitReached);

    /// <summary>A branch source as stored, with its anchor: it is the user's own, so nothing of it is held back.</summary>
    private static ExchangeBranchSource? Source(BranchSource source) =>
        source.SourceMessage is not { } message ? null
        : new ExchangeBranchSource(
            message,
            source.Quote,
            source is { SourceBlock: { } block, SourceOffset: { } offset } ? new ExchangePosition(block, offset) : null,
            source is { AnchorJson: { } anchor, AnchorFormatVersion: { } version, RenderProfile: { } profile } ? new ExchangeAnchor(version, profile, anchor) : null)
        {
            Block = source.SourceBlock,
            At = source.CreatedAt.ToUniversalTime(),
        };

    /// <summary>The saved versions; a candidate waiting to be adopted is not one, and an edit draft stays home.</summary>
    private static ExchangeSummaryHistory History(SummaryDetail summary) => new(
        summary.Node.Id,
        summary.Node.DisplayName,
        summary.Node.NameSource,
        summary.Node.BaseTitle,
        summary.Node.Current,
        summary.Node.CreatedAt.ToUniversalTime(),
        [.. summary.Saved.Select(v => new ExchangeSummaryVersion(
            v.Id, v.Seq, v.Title, v.ShortTitle, v.Body, v.ToSeq, v.Origin, v.Baseline, v.Model, v.CreatedAt.ToUniversalTime(),
            summary.ReadStates.GetValueOrDefault(v.Id)?.Source))]);

    private static ExchangeResource Resource(StoredResource stored, StoredDerivative? text)
    {
        var r = stored.Resource;
        return new ExchangeResource(r.Id, r.ConversationId, r.Hash, r.MediaType, r.Size, r.Origin, r.Name, r.SourceUrl, r.Provenance, r.CreatedAt.ToUniversalTime())
        {
            Text = text?.Derivative.Text ?? stored.Text,
            TextTruncated = text?.Derivative.Truncated ?? stored.TextTruncated,
            TextProducer = text?.Derivative.Producer,
        };
    }

    /// <summary>The material a message refers to: in its blocks, and as the result of a step of its run.</summary>
    public static IEnumerable<ResourceId> Referred(ExchangeMessage message)
    {
        foreach (var block in message.Stored?.Blocks ?? [])
        {
            if (block switch
            {
                AttachmentBlock a => a.Resource,
                ToolResultBlock { Resource: { } result } => result,
                CitationBlock { Resource: { } cited } => cited,
                _ => (ResourceId?)null,
            } is { } id)
            {
                yield return id;
            }
        }

        foreach (var step in message.Stored?.Run?.Steps ?? [])
        {
            if (step.Result is { } result)
            {
                yield return result;
            }
        }
    }
}
