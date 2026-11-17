using System.IO.Compression;
using System.Text;
using System.Xml;
using Mindweave.Application.Resources;
using Mindweave.Core.Resources;

namespace Mindweave.Infrastructure.Resources;

/// <summary>
/// The text of a Word document (.docx; requirements §10.4.3, architecture §15.1), read from <c>word/document.xml</c>
/// without an Office library: one line per paragraph, a tab for <c>w:tab</c>, a line break for <c>w:br</c> and
/// <c>w:cr</c>, table cells separated by tabs and rows by line breaks. Deleted text (tracked changes), field codes and
/// the fallback copy of alternate content are left out. The archive is bounded: a document part that would expand beyond
/// <see cref="MaxDocumentBytes"/>, or expands implausibly far, is <see cref="ExtractionFailure.TooLarge"/>. A password-
/// protected .docx is an OLE compound file, not a zip, and is <see cref="ExtractionFailure.Encrypted"/>.
/// </summary>
internal sealed class DocxTextExtractor : ITextExtractor
{
    internal const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private const string MarkupCompatibilityNamespace = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    /// <summary>The most the document part may expand to (200 MB).</summary>
    internal const long MaxDocumentBytes = 200L * 1000 * 1000;

    /// <summary>Above this size, a part expanding more than <see cref="MaxCompressionRatio"/> times is taken for a zip bomb.</summary>
    private const long RatioCheckedFrom = 10L * 1000 * 1000;

    private const long MaxCompressionRatio = 100;

    private static readonly byte[] OleSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>Elements whose content is formatting or a duplicate, never document text.</summary>
    private static readonly HashSet<string> SkippedWordElements =
        ["pPr", "rPr", "tblPr", "tblGrid", "trPr", "tcPr", "sectPr", "del", "delText", "moveFrom", "instrText", "delInstrText", "fldData"];

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CloseInput = false,
    };

    public string Producer => "docx/1";

    public bool CanExtract(string mediaType) => string.Equals(mediaType, MediaTypes.Docx, StringComparison.Ordinal);

    public TextExtraction Extract(byte[] original, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (original.AsSpan().StartsWith(OleSignature))
        {
            return TextExtraction.Failed(ExtractionFailure.Encrypted);
        }

        try
        {
            using var archive = new ZipArchive(new MemoryStream(original, writable: false), ZipArchiveMode.Read);
            var entry = archive.GetEntry("word/document.xml");
            if (entry is null)
            {
                return TextExtraction.Failed(ExtractionFailure.NotReadable);
            }

            if (entry.Length > MaxDocumentBytes
                || (entry.Length > RatioCheckedFrom && entry.Length > MaxCompressionRatio * Math.Max(entry.CompressedLength, 1)))
            {
                return TextExtraction.Failed(ExtractionFailure.TooLarge);
            }

            using var stream = new BoundedReadStream(entry.Open(), MaxDocumentBytes);
            var (text, truncated) = Read(stream);
            text = text.TrimEnd();
            if (text.All(char.IsWhiteSpace))
            {
                return TextExtraction.Failed(ExtractionFailure.NoText);
            }

            var bounded = ResourceLimits.Bounded(text);
            return truncated ? bounded with { Truncated = true } : bounded;
        }
        catch (BoundedReadStream.LimitExceededException)
        {
            return TextExtraction.Failed(ExtractionFailure.TooLarge);
        }
#pragma warning disable CA1031 // A damaged archive or document part can fail anywhere in the zip or XML reader; every such failure means "not readable".
        catch (Exception)
#pragma warning restore CA1031
        {
            return TextExtraction.Failed(ExtractionFailure.NotReadable);
        }
    }

    private static (string Text, bool Truncated) Read(Stream stream)
    {
        var text = new StringBuilder();
        var openCells = 0;
        using var reader = XmlReader.Create(stream, ReaderSettings);
        reader.MoveToContent();
        while (!reader.EOF)
        {
            if (text.Length > ResourceLimits.MaxExtractedCharacters)
            {
                return (text.ToString(), true);
            }

            var word = reader.NamespaceURI == WordNamespace;
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.NamespaceURI == MarkupCompatibilityNamespace && reader.LocalName == "Fallback")
                {
                    reader.Skip();
                    continue;
                }

                if (word && SkippedWordElements.Contains(reader.LocalName))
                {
                    reader.Skip();
                    continue;
                }

                if (word && reader.LocalName == "t")
                {
                    text.Append(reader.ReadElementContentAsString());
                    continue;
                }

                if (word)
                {
                    switch (reader.LocalName)
                    {
                        case "tab":
                            text.Append('\t');
                            break;
                        case "br" or "cr":
                            text.Append('\n');
                            break;
                        case "noBreakHyphen":
                            text.Append('-');
                            break;
                        case "p" when reader.IsEmptyElement:
                            EndParagraph(text, openCells);
                            break;
                        case "tc" when !reader.IsEmptyElement:
                            openCells++;
                            break;
                    }
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && word)
            {
                switch (reader.LocalName)
                {
                    case "p":
                        EndParagraph(text, openCells);
                        break;
                    case "tc":
                        openCells = Math.Max(openCells - 1, 0);
                        TrimEndSpaces(text);
                        text.Append('\t');
                        break;
                    case "tr":
                        if (text.Length > 0 && text[^1] == '\t')
                        {
                            text.Length--;
                        }

                        text.Append('\n');
                        break;
                }
            }

            reader.Read();
        }

        return (text.ToString(), false);
    }

    /// <summary>A paragraph ends its line; paragraphs inside a table cell stay on the row's line.</summary>
    private static void EndParagraph(StringBuilder text, int openCells) => text.Append(openCells > 0 ? ' ' : '\n');

    private static void TrimEndSpaces(StringBuilder text)
    {
        while (text.Length > 0 && text[^1] == ' ')
        {
            text.Length--;
        }
    }

    /// <summary>Reads at most a set number of bytes, whatever the archive's header claims the entry expands to.</summary>
    private sealed class BoundedReadStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private int Count(int read)
        {
            _read += read;
            return _read > limit ? throw new LimitExceededException() : read;
        }

        internal sealed class LimitExceededException : IOException
        {
        }
    }
}
