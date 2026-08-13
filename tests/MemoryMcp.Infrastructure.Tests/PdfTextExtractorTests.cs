using AwesomeAssertions;
using MemoryMcp.Application.Abstractions;
using MemoryMcp.Infrastructure.Documents;

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

    [Fact]
    public async Task ExtractTextAsync_returns_the_pdfs_text_content()
    {
        var extractor = new PdfTextExtractor();
        var bytes = System.Text.Encoding.ASCII.GetBytes(MinimalPdf);

        var text = await extractor.ExtractTextAsync(bytes);

        text.Should().Contain("Hello World");
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
