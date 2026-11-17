using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;

namespace Mindweave.Core.Context;

/// <summary>How an attachment is sent in one request (architecture §15.4).</summary>
public enum ResourceForm
{
    /// <summary>Its whole text.</summary>
    Whole,

    /// <summary>Its summary only (an older question's attachment).</summary>
    Summary,

    /// <summary>Its summary and the chunks most relevant to the question (the current question's attachment).</summary>
    Parts,

    /// <summary>An image as an image part of the request.</summary>
    Image,

    /// <summary>An older image left out for room; a line says so.</summary>
    ImageOmitted,

    /// <summary>An image the model cannot see; a line says so.</summary>
    ImageUnsupported,

    /// <summary>An image whose original is missing; a line says so.</summary>
    ImageMissing,
}

/// <summary>What an attachment can be sent as, from the planner's point of view.</summary>
public enum ResourceKind
{
    Text,

    /// <summary>An image the model can see, whose original is there.</summary>
    Image,

    /// <summary>An image the model cannot see.</summary>
    ImageUnsupported,

    /// <summary>An image whose original file is gone.</summary>
    ImageMissing,
}

/// <summary>
/// One attachment of a planned message as the planner sees it. Sizes are estimated tokens of the material texts
/// (<see cref="ResourceMaterial"/>): <paramref name="WholeTokens"/> of the whole form (for an image: the image or its
/// placeholder line), <paramref name="SummaryTokens"/> of the summary form when a summary is stored (null: not made yet),
/// <paramref name="RankedChunks"/> the text's chunks, most relevant to the current question first.
/// </summary>
public sealed record ResourceCandidate(
    AttachmentBlock Attachment, MessageId Message, ResourceKind Kind, int WholeTokens, int? SummaryTokens, IReadOnlyList<ResourceChunk> RankedChunks)
{
    /// <summary>Estimated tokens of an attachment summary not written yet (it is asked for in at most 500 characters).</summary>
    public const int UnknownSummaryTokens = 900;

    public int SummaryOrAssumed => SummaryTokens ?? UnknownSummaryTokens;
}

/// <summary>
/// How one attachment goes out. <paramref name="Chunks"/> are the chunk ordinals sent with <see cref="ResourceForm.Parts"/>,
/// in text order, of <paramref name="ChunkCount"/>. <paramref name="NeedsSummary"/> when its form uses a summary not made yet.
/// </summary>
public sealed record ResourcePlacement(AttachmentBlock Attachment, MessageId Message, ResourceForm Form, IReadOnlyList<int> Chunks, int ChunkCount, bool NeedsSummary)
{
    public ResourceId Resource => Attachment.Resource;

    /// <summary>Not sent as it is: a summary, parts, or an older image left out.</summary>
    public bool IsReduced => Form is ResourceForm.Summary or ResourceForm.Parts or ResourceForm.ImageOmitted;
}

/// <summary>The attachment layer of one plan: the candidates of the planned messages and the form each has reached.</summary>
internal sealed class ResourceLayer
{
    private readonly List<Entry> _entries;

    public ResourceLayer(IReadOnlyList<ContextItem> items, IReadOnlyList<ResourceCandidate> candidates)
    {
        var order = items.Select((item, index) => (item.Message.Id, index)).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().index);
        var current = items.Count > 0 ? items[^1].Message.Id : default;
        _entries = [.. candidates
            .Where(c => order.ContainsKey(c.Message))
            .Select((c, i) => (c, i))
            .OrderBy(x => order[x.c.Message]).ThenBy(x => x.i)
            .Select(x => new Entry(x.c, x.c.Message == current))];
    }

    public int Count => _entries.Count;

    public int Tokens => _entries.Sum(e => e.Tokens);

    /// <summary>The least the attachments can cost: older ones at their smallest form, current ones as summary and one chunk.</summary>
    public int MinimalTokens => _entries.Sum(e => e.Minimal);

    public int ReduceOlder(int total, int budget)
    {
        foreach (var entry in _entries.Where(e => !e.Current))
        {
            if (total <= budget)
            {
                break;
            }

            var before = entry.Tokens;
            entry.ReduceOlder();
            total += entry.Tokens - before;
        }

        return total;
    }

    /// <summary>
    /// Fits the current question's text attachments into the room the rest leaves: the smaller ones whole while each fits its
    /// fair share, the rest as summary and parts, the room split by size and each taking at least one chunk, then any room
    /// left filled by relevance.
    /// </summary>
    public int ReduceCurrent(int total, int budget)
    {
        var texts = _entries.Where(e => e.Current && e.Candidate.Kind == ResourceKind.Text && e.Candidate.RankedChunks.Count > 0).ToList();
        if (texts.Count == 0)
        {
            return total;
        }

        var rest = total - texts.Sum(e => e.Tokens);
        var room = budget - rest;
        var reduced = new List<Entry>();
        var remaining = room;
        var left = texts.Count;
        foreach (var entry in texts.OrderBy(e => e.Candidate.WholeTokens))
        {
            if (entry.Candidate.WholeTokens <= remaining / left)
            {
                remaining -= entry.Candidate.WholeTokens;
            }
            else
            {
                reduced.Add(entry);
            }

            left--;
        }

        var sizes = reduced.Sum(e => (long)e.Candidate.WholeTokens);
        foreach (var entry in reduced)
        {
            var share = (int)(remaining * (long)entry.Candidate.WholeTokens / Math.Max(1, sizes));
            entry.ToParts(share);
        }

        // Room some attachment did not use goes to the others, by relevance.
        var used = texts.Sum(e => e.Tokens);
        foreach (var entry in reduced)
        {
            used += entry.Grow(room - used);
        }

        return rest + texts.Sum(e => e.Tokens);
    }

    public IReadOnlyList<ResourcePlacement> Placements() => [.. _entries.Select(e => e.Placement())];

    private sealed class Entry(ResourceCandidate candidate, bool current)
    {
        private readonly List<ResourceChunk> _chunks = [];

        public ResourceCandidate Candidate { get; } = candidate;

        public bool Current { get; } = current;

        public ResourceForm Form { get; private set; } = candidate.Kind switch
        {
            ResourceKind.Text => ResourceForm.Whole,
            ResourceKind.Image => ResourceForm.Image,
            ResourceKind.ImageUnsupported => ResourceForm.ImageUnsupported,
            _ => ResourceForm.ImageMissing,
        };

        public int Tokens => TokensOf(Form);

        public int Minimal
        {
            get
            {
                if (Candidate.Kind != ResourceKind.Text)
                {
                    return Current || Candidate.Kind != ResourceKind.Image ? Candidate.WholeTokens : OmittedTokens;
                }

                if (!Current)
                {
                    return Math.Min(Candidate.WholeTokens, Candidate.SummaryOrAssumed);
                }

                return Candidate.RankedChunks.Count == 0
                    ? Candidate.WholeTokens
                    : Math.Min(Candidate.WholeTokens, PartsBase + ResourceMaterial.ChunkTokens(Candidate.RankedChunks[0]));
            }
        }

        private int PartsBase => Candidate.SummaryOrAssumed + ResourceMaterial.PartsOverheadTokens;

        private int OmittedTokens => TokenEstimator.Estimate(ResourceMaterial.ImageOmitted(Candidate.Attachment));

        public void ReduceOlder()
        {
            if (Candidate.Kind == ResourceKind.Image)
            {
                Form = ResourceForm.ImageOmitted;
            }
            else if (Candidate.Kind == ResourceKind.Text && Candidate.SummaryOrAssumed < Candidate.WholeTokens)
            {
                Form = ResourceForm.Summary;
            }
        }

        /// <summary>Summary and the most relevant chunks within <paramref name="share"/>, at least one chunk.</summary>
        public void ToParts(int share)
        {
            _chunks.Clear();
            var size = PartsBase;
            foreach (var chunk in Candidate.RankedChunks)
            {
                var cost = ResourceMaterial.ChunkTokens(chunk);
                if (_chunks.Count > 0 && size + cost > share)
                {
                    continue;
                }

                _chunks.Add(chunk);
                size += cost;
            }

            Form = ResourceForm.Parts;
            KeepWholeWhenCheaper();
        }

        /// <summary>Adds the next relevant chunks while they fit <paramref name="room"/>; returns the tokens added.</summary>
        public int Grow(int room)
        {
            if (Form != ResourceForm.Parts || room <= 0)
            {
                return 0;
            }

            var before = Tokens;
            foreach (var chunk in Candidate.RankedChunks.Except(_chunks).ToList())
            {
                var cost = ResourceMaterial.ChunkTokens(chunk);
                if (Tokens - before + cost <= room)
                {
                    _chunks.Add(chunk);
                }
            }

            KeepWholeWhenCheaper();
            return Tokens - before;
        }

        public ResourcePlacement Placement()
        {
            var needsSummary = Form is ResourceForm.Summary or ResourceForm.Parts && Candidate.SummaryTokens is null;
            return new ResourcePlacement(
                Candidate.Attachment,
                Candidate.Message,
                Form,
                Form == ResourceForm.Parts ? [.. _chunks.Select(c => c.Ordinal).Order()] : [],
                Candidate.RankedChunks.Count,
                needsSummary);
        }

        private void KeepWholeWhenCheaper()
        {
            if (Form == ResourceForm.Parts && (_chunks.Count == Candidate.RankedChunks.Count || TokensOf(ResourceForm.Parts) >= Candidate.WholeTokens))
            {
                Form = ResourceForm.Whole;
                _chunks.Clear();
            }
        }

        private int TokensOf(ResourceForm form) => form switch
        {
            ResourceForm.Summary => Candidate.SummaryOrAssumed,
            ResourceForm.Parts => PartsBase + _chunks.Sum(ResourceMaterial.ChunkTokens),
            ResourceForm.ImageOmitted => OmittedTokens,
            _ => Candidate.WholeTokens,
        };
    }
}
