using System.Text.Json;
using Mindweave.Application.Exchange;
using Mindweave.Core.Conversations;
using Mindweave.Infrastructure.Share;

namespace Mindweave.Infrastructure.ManualBackup;

/// <summary>
/// The fields only a manual backup's document has (docs/design/sharing-backup.md §1), as optional fields of version 1 of the
/// format (E5). Each value is written by name, never by serializing a stored record (E5): a new kind of block must be
/// added here on purpose before a backup can hold it.
/// </summary>
internal static class ManualBackupJson
{
    public static void Write(Utf8JsonWriter json, ExchangeStoredMessage message)
    {
        json.WriteString("created_at", SharePage.Instant(message.CreatedAt));
        if (message.CompletedAt is { } completed)
        {
            json.WriteString("completed_at", SharePage.Instant(completed));
        }

        SharePage.WriteIfAny(json, "supersedes", message.Supersedes?.ToString());
        SharePage.WriteIfAny(json, "error", message.Error is { } error ? SharePage.Name(error) : null);
        SharePage.WriteIfAny(json, "model", message.Model);
        json.WriteStartArray("blocks");
        foreach (var block in message.Blocks)
        {
            Write(json, block);
        }

        json.WriteEndArray();
        if (message.Run is { } run)
        {
            json.WriteStartObject("run");
            SharePage.WriteIfAny(json, "limit_reached", run.LimitReached is { } limit ? SharePage.Name(limit) : null);
            json.WriteStartArray("steps");
            foreach (var step in run.Steps)
            {
                Write(json, step);
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }
    }

    public static void Write(Utf8JsonWriter json, ExchangeSummaryHistory history)
    {
        json.WriteStartObject("summary_history");
        json.WriteString("node", history.Node.ToString());
        json.WriteString("name", history.Name);
        json.WriteString("name_source", SharePage.Name(history.NameSource));
        json.WriteString("base_title", history.BaseTitle);
        json.WriteString("current", history.Current.ToString());
        json.WriteString("created_at", SharePage.Instant(history.CreatedAt));
        json.WriteStartArray("versions");
        foreach (var version in history.Versions)
        {
            json.WriteStartObject();
            json.WriteString("id", version.Id.ToString());
            json.WriteNumber("seq", version.Seq);
            json.WriteString("title", version.Title);
            SharePage.WriteIfAny(json, "short_title", version.ShortTitle);
            json.WriteString("body", version.Body);
            json.WriteNumber("to_seq", version.ToSeq);
            json.WriteString("origin", SharePage.Name(version.Origin));
            SharePage.WriteIfAny(json, "baseline", version.Baseline?.ToString());
            SharePage.WriteIfAny(json, "model", version.Model);
            json.WriteString("at", SharePage.Instant(version.At));
            SharePage.WriteIfAny(json, "read", version.Read is { } read ? SharePage.Name(read) : null);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    /// <param name="original">Whether its original is in the package: present, missing or damaged.</param>
    public static void Write(Utf8JsonWriter json, ExchangeResource resource, string? original)
    {
        json.WriteStartObject();
        json.WriteString("id", resource.Id.ToString());
        SharePage.WriteIfAny(json, "conversation", resource.Conversation?.ToString());
        json.WriteString("hash", resource.Hash.Hex);
        json.WriteString("media_type", resource.MediaType);
        json.WriteNumber("size", resource.Size);
        json.WriteString("origin", SharePage.Name(resource.Origin));
        SharePage.WriteIfAny(json, "name", resource.Name);
        SharePage.WriteIfAny(json, "source_url", resource.SourceUrl?.AbsoluteUri);
        json.WriteString("provenance", SharePage.Name(resource.Provenance));
        json.WriteString("created_at", SharePage.Instant(resource.CreatedAt));
        SharePage.WriteIfAny(json, "text", resource.Text);
        if (resource.TextTruncated)
        {
            json.WriteBoolean("text_truncated", true);
        }

        SharePage.WriteIfAny(json, "text_producer", resource.TextProducer);
        SharePage.WriteIfAny(json, "original", original);
        json.WriteEndObject();
    }

    private static void Write(Utf8JsonWriter json, ExchangeRunStep step)
    {
        json.WriteStartObject();
        json.WriteNumber("ordinal", step.Ordinal);
        json.WriteString("kind", SharePage.Name(step.Kind));
        json.WriteString("state", SharePage.Name(step.State));
        json.WriteString("started_at", SharePage.Instant(step.StartedAt));
        if (step.FinishedAt is { } finished)
        {
            json.WriteString("finished_at", SharePage.Instant(finished));
        }

        SharePage.WriteIfAny(json, "call_id", step.CallId);
        SharePage.WriteIfAny(json, "tool", step.Tool);
        SharePage.WriteIfAny(json, "risk", step.Risk is { } risk ? SharePage.Name(risk) : null);
        SharePage.WriteIfAny(json, "input", step.Input);
        SharePage.WriteIfAny(json, "result", step.Result?.ToString());
        SharePage.WriteIfAny(json, "error", step.Error is { } error ? SharePage.Name(error) : null);
        SharePage.WriteIfAny(json, "approval", step.Approval is { } approval ? SharePage.Name(approval) : null);
        json.WriteEndObject();
    }

    private static void Write(Utf8JsonWriter json, ContentBlock block)
    {
        json.WriteStartObject();
        switch (block)
        {
            case TextBlock text:
                json.WriteString("type", "text");
                json.WriteString("markdown", text.Markdown);
                break;
            case AttachmentBlock attachment:
                // What it is (name, type, size, text) is the resource's, beside the messages.
                json.WriteString("type", "attachment");
                json.WriteString("resource", attachment.Resource.ToString());
                break;
            case AbsentAttachmentBlock absent:
                json.WriteString("type", "absent_attachment");
                SharePage.WriteIfAny(json, "name", absent.Name);
                break;
            case ToolCallBlock call:
                json.WriteString("type", "tool_call");
                json.WriteString("call_id", call.CallId);
                json.WriteString("tool", call.Tool);
                json.WriteString("arguments", call.Arguments);
                json.WriteString("risk", SharePage.Name(call.Risk));
                json.WriteString("summary", call.Summary);
                break;
            case ToolResultBlock result:
                json.WriteString("type", "tool_result");
                json.WriteString("call_id", result.CallId);
                json.WriteString("status", SharePage.Name(result.Status));
                json.WriteString("preview", result.Preview);
                SharePage.WriteIfAny(json, "resource", result.Resource?.ToString());
                SharePage.WriteIfAny(json, "detail", result.Detail);
                if (result.Findings is { Count: > 0 } findings)
                {
                    json.WriteStartArray("findings");
                    foreach (var finding in findings)
                    {
                        json.WriteStartObject();
                        json.WriteString("kind", SharePage.Name(finding.Kind));
                        json.WriteString("id", finding.Id);
                        json.WriteString("title", finding.Title);
                        if (finding.Part is { } part)
                        {
                            json.WriteNumber("part", part);
                        }

                        json.WriteEndObject();
                    }

                    json.WriteEndArray();
                }

                break;
            case ActivityCountBlock counts:
                json.WriteString("type", "activity");
                json.WriteNumber("attachment_searches", counts.AttachmentSearches);
                json.WriteNumber("summary_searches", counts.SummarySearches);
                json.WriteNumber("web_searches", counts.WebSearches);
                json.WriteNumber("pages_read", counts.PagesRead);
                json.WriteNumber("other_calls", counts.OtherCalls);
                break;
            case CitationBlock citation:
                json.WriteString("type", "citation");
                json.WriteNumber("number", citation.Number);
                json.WriteString("kind", SharePage.KindOf(citation.Kind));
                json.WriteString("title", citation.Title);
                SharePage.WriteIfAny(json, "url", citation.Url?.OriginalString);
                if (citation.FetchedAt is { } fetched)
                {
                    json.WriteString("fetched_at", SharePage.Instant(fetched));
                }

                SharePage.WriteIfAny(json, "resource", citation.Resource?.ToString());
                json.WriteNumber("start", citation.Start);
                json.WriteNumber("end", citation.End);
                SharePage.WriteIfAny(json, "target", citation.Target);
                break;
            default:
                throw new InvalidOperationException("A manual backup has no form for a " + block.GetType().Name + " yet.");
        }

        json.WriteEndObject();
    }
}
