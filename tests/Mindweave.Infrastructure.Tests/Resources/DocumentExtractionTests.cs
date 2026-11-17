using System.IO.Compression;
using System.Text;
using Mindweave.Application.Resources;
using Mindweave.Core.Resources;
using Mindweave.Infrastructure.Resources;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Mindweave.Infrastructure.Tests.Resources;

/// <summary>
/// PDF and Word text extraction (requirements §10.4.3, architecture §15.1). Fixtures are built in code: PDFs with PdfPig's
/// writer, .docx files as zip archives. A file that cannot be read is a failure with its reason, never an exception; a
/// scanned PDF (no text layer) is refused as having no text; a password-protected file says so.
/// </summary>
public sealed class DocumentExtractionTests
{
    private static readonly PdfTextExtractor Pdf = new();
    private static readonly DocxTextExtractor Docx = new();

    [Fact]
    public void The_file_types_name_pdf_and_docx_as_documents()
    {
        Assert.Equal(MediaTypes.Pdf, FileTypes.MediaTypeOf("Paper.PDF"));
        Assert.Equal(MediaTypes.Docx, FileTypes.MediaTypeOf("notes.docx"));
        Assert.Null(FileTypes.MediaTypeOf("old.doc"));
        Assert.True(FileTypes.IsDocumentMediaType(MediaTypes.Pdf));
        Assert.True(FileTypes.IsDocumentMediaType(MediaTypes.Docx));
        Assert.False(FileTypes.IsDocumentMediaType(MediaTypes.PlainText));
        Assert.False(FileTypes.IsTextMediaType(MediaTypes.Pdf));
        Assert.True(Pdf.CanExtract(MediaTypes.Pdf) && !Pdf.CanExtract(MediaTypes.Docx));
        Assert.True(Docx.CanExtract(MediaTypes.Docx) && !Docx.CanExtract(MediaTypes.Pdf));
    }

    [Fact]
    public void A_pdf_gives_its_text_page_by_page()
    {
        var bytes = BuildPdf(["First page line one", "First page line two"], ["Second page text"]);

        var result = Pdf.Extract(bytes, MediaTypes.Pdf);

        Assert.Null(result.Failure);
        Assert.False(result.Truncated);
        var text = result.Text!;
        Assert.DoesNotContain('\r', text);
        Assert.Contains("First page line one", text, StringComparison.Ordinal);
        Assert.Contains("First page line two", text, StringComparison.Ordinal);
        Assert.Contains("\n\nSecond page text", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("line one", StringComparison.Ordinal) < text.IndexOf("line two", StringComparison.Ordinal));
    }

    [Fact]
    public void A_pdf_longer_than_the_page_limit_is_cut_there_and_marked_truncated()
    {
        var bytes = BuildPdf(["Page one has some text"], ["Page two has some text"], ["Page three has some text"]);

        var result = new PdfTextExtractor(maxPages: 2).Extract(bytes, MediaTypes.Pdf);

        Assert.True(result.Truncated);
        Assert.Contains("Page two", result.Text!, StringComparison.Ordinal);
        Assert.DoesNotContain("Page three", result.Text!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pdf_without_a_text_layer_has_no_text()
    {
        Assert.Equal(ExtractionFailure.NoText, Pdf.Extract(BuildPdf([]), MediaTypes.Pdf).Failure);

        // A page number on each page of a scan is not a text layer.
        Assert.Equal(ExtractionFailure.NoText, Pdf.Extract(BuildPdf(["1"], ["2"], ["3"]), MediaTypes.Pdf).Failure);
    }

    [Fact]
    public void Bytes_that_are_no_pdf_or_a_damaged_one_are_not_readable()
    {
        Assert.Equal(ExtractionFailure.NotReadable, Pdf.Extract(Encoding.UTF8.GetBytes("plain words, not a PDF"), MediaTypes.Pdf).Failure);
        Assert.Equal(ExtractionFailure.NotReadable, Pdf.Extract([], MediaTypes.Pdf).Failure);
        Assert.Equal(ExtractionFailure.NotReadable, Pdf.Extract(Encoding.ASCII.GetBytes("%PDF-1.7\n"), MediaTypes.Pdf).Failure);

        var whole = BuildPdf(["Some text that will be cut away"]);
        Assert.NotNull(Pdf.Extract(whole[..(whole.Length / 3)], MediaTypes.Pdf).Failure);
    }

    [Fact]
    public void A_pdf_that_needs_a_user_password_is_encrypted()
    {
        Assert.Equal(ExtractionFailure.Encrypted, Pdf.Extract(EncryptedPdf(), MediaTypes.Pdf).Failure);
    }

    [Fact]
    public void A_pdf_with_chinese_text_in_an_embedded_font_gives_the_chinese_text()
    {
        var font = new[] { "/System/Library/Fonts/Supplemental/Arial Unicode.ttf", "/Library/Fonts/Arial Unicode.ttf" }.FirstOrDefault(File.Exists);
        Assert.SkipWhen(font is null, "No TrueType font with Chinese glyphs on this machine (macOS ships Arial Unicode).");

        var builder = new PdfDocumentBuilder();
        var added = builder.AddTrueTypeFont(File.ReadAllBytes(font!));
        builder.AddPage(PageSize.A4).AddText("知识网络与对话", 14, new PdfPoint(50, 700), added);

        var result = Pdf.Extract(builder.Build(), MediaTypes.Pdf);

        Assert.Null(result.Failure);
        Assert.Contains("知识网络与对话", result.Text!.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void A_docx_gives_paragraphs_tabs_breaks_and_tables_in_order()
    {
        var bytes = BuildDocx("""
            <w:p><w:pPr><w:pStyle w:val="Heading1"/><w:tabs><w:tab w:val="left" w:pos="720"/></w:tabs></w:pPr><w:r><w:t>第一章 Introduction</w:t></w:r></w:p>
            <w:p><w:r><w:t xml:space="preserve">Name:</w:t></w:r><w:r><w:tab/><w:t>Mindweave</w:t></w:r></w:p>
            <w:p><w:r><w:t>上一行</w:t><w:br/><w:t>下一行</w:t></w:r></w:p>
            <w:p><w:r><w:t xml:space="preserve">Kept </w:t></w:r><w:del w:id="1" w:author="a"><w:r><w:delText>removed </w:delText></w:r></w:del><w:r><w:t>text</w:t></w:r></w:p>
            <w:tbl><w:tblPr/><w:tblGrid><w:gridCol/><w:gridCol/></w:tblGrid>
              <w:tr><w:tc><w:tcPr/><w:p><w:r><w:t>名称</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Value</w:t></w:r></w:p></w:tc></w:tr>
              <w:tr><w:tc><w:p><w:r><w:t>甲</w:t></w:r></w:p><w:p><w:r><w:t>乙</w:t></w:r></w:p></w:tc><w:tc><w:p/></w:tc></w:tr>
            </w:tbl>
            <w:p><w:r><w:t>The end.</w:t></w:r></w:p>
            """);

        var result = Docx.Extract(bytes, MediaTypes.Docx);

        Assert.Null(result.Failure);
        Assert.Equal("第一章 Introduction\nName:\tMindweave\n上一行\n下一行\nKept text\n名称\tValue\n甲 乙\t\nThe end.", result.Text);
    }

    [Fact]
    public void A_docx_with_only_empty_paragraphs_has_no_text()
    {
        Assert.Equal(ExtractionFailure.NoText, Docx.Extract(BuildDocx("<w:p/><w:p><w:r><w:t xml:space=\"preserve\">  </w:t></w:r></w:p>"), MediaTypes.Docx).Failure);
    }

    [Fact]
    public void A_password_protected_docx_is_encrypted_and_other_bytes_are_not_readable()
    {
        byte[] ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Equal(ExtractionFailure.Encrypted, Docx.Extract(ole, MediaTypes.Docx).Failure);

        Assert.Equal(ExtractionFailure.NotReadable, Docx.Extract(Encoding.UTF8.GetBytes("not a zip at all"), MediaTypes.Docx).Failure);
        Assert.Equal(ExtractionFailure.NotReadable, Docx.Extract([0x50, 0x4B, 3, 4, 1, 2], MediaTypes.Docx).Failure);
        Assert.Equal(ExtractionFailure.NotReadable, Docx.Extract(Zip(("word/other.xml", "<x/>")), MediaTypes.Docx).Failure);
        Assert.Equal(ExtractionFailure.NotReadable, Docx.Extract(Zip(("word/document.xml", "<w:document")), MediaTypes.Docx).Failure);
    }

    [Fact]
    public void A_docx_with_a_dtd_is_not_read()
    {
        var xml = $"""<?xml version="1.0"?><!DOCTYPE d [<!ENTITY e "boom">]><w:document xmlns:w="{DocxTextExtractor.WordNamespace}"><w:body><w:p><w:r><w:t>&e;</w:t></w:r></w:p></w:body></w:document>""";

        Assert.Equal(ExtractionFailure.NotReadable, Docx.Extract(Zip(("word/document.xml", xml)), MediaTypes.Docx).Failure);
    }

    [Fact]
    public void A_docx_whose_document_expands_implausibly_far_is_too_large()
    {
        // 30 MB of one repeated character compresses about a thousandfold: a zip bomb's signature.
        var xml = new StringBuilder($"""<w:document xmlns:w="{DocxTextExtractor.WordNamespace}"><w:body><w:p><w:r><w:t>""")
            .Append('a', 30_000_000).Append("</w:t></w:r></w:p></w:body></w:document>").ToString();

        Assert.Equal(ExtractionFailure.TooLarge, Docx.Extract(Zip(("word/document.xml", xml)), MediaTypes.Docx).Failure);
    }

    private static byte[] BuildPdf(params string[][] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        if (pages.Length == 0)
        {
            builder.AddPage(PageSize.A4);
        }

        foreach (var lines in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            for (var i = 0; i < lines.Length; i++)
            {
                page.AddText(lines[i], 12, new PdfPoint(50, 750 - (20 * i)), font);
            }
        }

        return builder.Build();
    }

    /// <summary>
    /// A one-page PDF with a standard security handler (RC4, revision 2) whose user password is not empty: the stored
    /// /U value matches no password the reader can try, which is how a file that asks for a password looks to it.
    /// </summary>
    private static byte[] EncryptedPdf()
    {
        var u = string.Concat(Enumerable.Range(0, 32).Select(i => ((i * 37) + 11).ToString("x2", System.Globalization.CultureInfo.InvariantCulture)[^2..]));
        var o = string.Concat(Enumerable.Range(0, 32).Select(i => ((i * 53) + 7).ToString("x2", System.Globalization.CultureInfo.InvariantCulture)[^2..]));
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 5 0 R >>",
            $"<< /Filter /Standard /V 1 /R 2 /O <{o}> /U <{u}> /P -44 >>",
            "<< /Length 8 >>\nstream\n\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008\nendstream",
        ];
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(System.Globalization.CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append(System.Globalization.CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(System.Globalization.CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        const string id = "0123456789abcdef0123456789abcdef";
        pdf.Append(System.Globalization.CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R /Encrypt 4 0 R /ID [<{id}> <{id}>] >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    private static byte[] BuildDocx(string body) =>
        Zip(
            ("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>"""),
            ("word/document.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="{DocxTextExtractor.WordNamespace}"><w:body>{body}<w:sectPr><w:pgSz w:w="11906" w:h="16838"/></w:sectPr></w:body></w:document>"""));

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        return buffer.ToArray();
    }
}
