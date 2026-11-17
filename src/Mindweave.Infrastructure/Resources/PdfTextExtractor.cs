using System.Text;
using Mindweave.Application.Resources;
using Mindweave.Core.Resources;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace Mindweave.Infrastructure.Resources;

/// <summary>
/// The text layer of a PDF (requirements §10.4.3, architecture §15.1), page by page in content order; paragraphs and pages
/// are separated by a blank line. At most <see cref="ResourceLimits.MaxPdfPages"/> pages are read. A PDF that needs a
/// password is <see cref="ExtractionFailure.Encrypted"/>; one that opens but has fewer than
/// <see cref="MinCharactersPerPage"/> visible characters per page on average has no text layer worth sending (a scanned
/// document; no OCR is attempted) and is <see cref="ExtractionFailure.NoText"/>.
/// </summary>
internal sealed class PdfTextExtractor : ITextExtractor
{
    /// <summary>Visible characters a page must average; below this the few found are page numbers or scanner stamps.</summary>
    internal const int MinCharactersPerPage = 4;

    private static readonly ContentOrderTextExtractor.Options PageOptions = new()
    {
        SeparateParagraphsWithDoubleNewline = true,
        ReplaceWhitespaceWithSpace = true,
    };

    private readonly int _maxPages;

    public PdfTextExtractor()
        : this(ResourceLimits.MaxPdfPages)
    {
    }

    internal PdfTextExtractor(int maxPages) => _maxPages = maxPages;

    public string Producer => "pdfpig/1";

    public bool CanExtract(string mediaType) => string.Equals(mediaType, MediaTypes.Pdf, StringComparison.Ordinal);

    public TextExtraction Extract(byte[] original, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (!HasPdfHeader(original))
        {
            return TextExtraction.Failed(ExtractionFailure.NotReadable);
        }

        try
        {
            using var document = PdfDocument.Open(original, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            var pages = Math.Min(document.NumberOfPages, _maxPages);
            var cut = document.NumberOfPages > pages;
            var text = new StringBuilder();
            var visible = 0L;
            for (var number = 1; number <= pages; number++)
            {
                var pageText = PlainTextExtractor.NormalizeLineEndings(ContentOrderTextExtractor.GetText(document.GetPage(number), PageOptions)).Trim();
                if (pageText.Length == 0)
                {
                    continue;
                }

                visible += pageText.Count(c => !char.IsWhiteSpace(c));
                if (text.Length > 0)
                {
                    text.Append("\n\n");
                }

                text.Append(pageText);
                if (text.Length > ResourceLimits.MaxExtractedCharacters)
                {
                    cut |= number < pages;
                    break;
                }
            }

            if (visible == 0 || visible < (long)MinCharactersPerPage * Math.Max(pages, 1))
            {
                return TextExtraction.Failed(ExtractionFailure.NoText);
            }

            var bounded = ResourceLimits.Bounded(text.ToString());
            return cut ? bounded with { Truncated = true } : bounded;
        }
        catch (PdfDocumentEncryptedException)
        {
            return TextExtraction.Failed(ExtractionFailure.Encrypted);
        }
#pragma warning disable CA1031 // A damaged file can fail anywhere inside the parser; every such failure means "not readable".
        catch (Exception)
#pragma warning restore CA1031
        {
            return TextExtraction.Failed(ExtractionFailure.NotReadable);
        }
    }

    /// <summary>"%PDF-" within the first kilobyte, where readers look for it (some files carry a few bytes before it).</summary>
    private static bool HasPdfHeader(ReadOnlySpan<byte> bytes) => bytes[..Math.Min(bytes.Length, 1024)].IndexOf("%PDF-"u8) >= 0;
}
