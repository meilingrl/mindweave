using Mindweave.Application.Share;
using Mindweave.Core.Branches;

namespace Mindweave.Testing;

/// <summary>Stands in for the app's Markdown, which Infrastructure does not have: every text is one paragraph.</summary>
public sealed class PlainMarkdown : IShareMarkdown
{
    public IReadOnlyList<ShareNode> Draw(string markdown, IReadOnlySet<int> citations, IReadOnlyList<ShareMark> marks) =>
        markdown.Contains('$', StringComparison.Ordinal)
            ? [new ShareNode("p", [ShareNode.OfText(markdown)]), new ShareNode("mathblock", [], markdown.Trim('$'))]
            : [new ShareNode("p", [ShareNode.OfText(markdown)])];

    public int? OffsetOf(BranchSource source) => null;
}
