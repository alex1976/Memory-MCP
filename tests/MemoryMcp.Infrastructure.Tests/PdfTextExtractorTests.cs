using AwesomeAssertions;
using MemoryMcp.Application.Abstractions;
using MemoryMcp.Infrastructure.Documents;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace MemoryMcp.Infrastructure.Tests;

/// <summary>
/// Unlike the repository tests in this project, <see cref="PdfTextExtractor"/> has no Postgres
/// dependency (PdfPig parses entirely in-process), so these run standalone without <see cref="PostgresFixture"/>.
/// </summary>
public sealed class PdfTextExtractorTests
{
    // A hand-written minimal single-page PDF ("Hello World" via a Tj operator) — PdfPig recovers the
    // page tree even though the xref byte offsets below are approximate rather than byte-exact, the
    // same tolerance real-world PDF producers rely on.
    private const string MinimalPdf = """
        %PDF-1.1
        1 0 obj  << /Type /Catalog /Pages 2 0 R >> endobj
        2 0 obj  << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj
        3 0 obj  << /Type /Page /Parent 2 0 R /Resources << /Font << /F1 4 0 R >> >> /MediaBox [0 0 300 144] /Contents 5 0 R >> endobj
        4 0 obj  << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> endobj
        5 0 obj  << /Length 44 >>
        stream
        BT /F1 18 Tf 0 0 Td (Hello World) Tj ET
        endstream
        endobj
        xref
        0 6
        0000000000 65535 f
        0000000018 00000 n
        0000000077 00000 n
        0000000178 00000 n
        0000000457 00000 n
        0000000496 00000 n
        trailer  << /Root 1 0 R /Size 6 >>
        startxref
        625
        %%EOF
        """;

    /// <summary>
    /// Builds a two-column page whose lines are drawn row by row (left cell, right cell, next row...) —
    /// what real producers emit for newspaper layouts and tables. In content-stream order this reads
    /// "Alpha one Beta one Alpha two Beta two...", so it pins the reading-order reconstruction.
    /// Generated with PdfPig's writer rather than hand-written bytes so the fixture is a valid PDF.
    /// </summary>
    private static byte[] BuildTwoColumnPdf()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(600, 400);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        var rows = new[] { "one", "two", "three", "four" };
        for (var i = 0; i < rows.Length; i++)
        {
            var y = 350 - (i * 20);
            page.AddText($"Alpha {rows[i]}", 12, new PdfPoint(50, y), font);
            page.AddText($"Beta {rows[i]}", 12, new PdfPoint(350, y), font);
        }

        return builder.Build();
    }

    [Fact]
    public async Task ExtractTextAsync_returns_the_pdfs_text_content()
    {
        var extractor = new PdfTextExtractor();
        var bytes = System.Text.Encoding.ASCII.GetBytes(MinimalPdf);

        var text = await extractor.ExtractTextAsync(bytes);

        text.Should().Contain("Hello World");
    }

    [Fact]
    public async Task ExtractTextAsync_reads_columns_top_to_bottom_rather_than_in_content_stream_order()
    {
        var extractor = new PdfTextExtractor();

        var text = await extractor.ExtractTextAsync(BuildTwoColumnPdf());

        // The whole left column must be read out before the right one starts, so no glyph of the second
        // column may appear before the last line of the first.
        text.IndexOf("Alpha four", StringComparison.Ordinal)
            .Should().BeGreaterThan(-1).And.BeLessThan(text.IndexOf("Beta one", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractTextAsync_throws_a_document_extraction_exception_for_non_pdf_bytes()
    {
        var extractor = new PdfTextExtractor();
        var garbage = "this is definitely not a pdf"u8.ToArray();

        var act = async () => await extractor.ExtractTextAsync(garbage);

        await act.Should().ThrowAsync<DocumentExtractionException>();
    }
}
