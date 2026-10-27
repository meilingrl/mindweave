using System.Text;
namespace Mindweave.Application.Summaries;

public static class SummaryTitleNormalization
{
    public static string GroupKey(string title)
    {
        KnowledgeSummaryProtocol.ValidateUnicode(title);
        return string.Join(" ", title.Normalize(NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    }
}
