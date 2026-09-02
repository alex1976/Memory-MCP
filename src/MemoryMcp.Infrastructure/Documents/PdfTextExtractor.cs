using System.Text;
using MemoryMcp.Application.Abstractions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace MemoryMcp.Infrastructure.Documents;

/// <summary>
/// Extracts text from a PDF entirely in-process via PdfPig (pure managed, no native dependencies) —
/// unlike <see cref="IEmbeddingProvider"/>/<see cref="IFactExtractor"/>, this needs no external
/// service/API key, so it's always available regardless of configuration.
/// </summary>
/// <remarks>
/// Text is recovered through PdfPig's document layout analysis rather than <see cref="Page.Text"/>:
/// that property concatenates glyphs in content-stream order, which interleaves columns, tables and
/// running headers into unreadable runs on any non-trivial layout. Since the extracted text is what
/// gets embedded and fed to the fact extractor, reading order is a correctness concern here, not a
/// cosmetic one. The pipeline groups letters into words, words into paragraph blocks, then sorts the
/// blocks into human reading order.
/// </remarks>
public sealed class PdfTextExtractor : IPdfTextExtractor
{
    // Lenient parsing plus SkipMissingFonts keep real-world (slightly malformed) PDFs readable
    // instead of throwing; UseActualText honours /ActualText replacements, which is what recovers
    // correct text from ligatures and from tables that draw glyphs out of logical order.
    private static readonly ParsingOptions Options = new()
    {
        UseLenientParsing = true,
        SkipMissingFonts = true,
        UseActualText = true,
    };

    public Task<string> ExtractTextAsync(byte[] pdfBytes, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                try
                {
                    using var document = PdfDocument.Open(pdfBytes, Options);
                    var text = new StringBuilder();
                    foreach (var page in document.GetPages())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (text.Length > 0)
                        {
                            text.AppendLine();
                        }

                        text.Append(ExtractPageText(page));
                    }

                    return text.ToString();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new DocumentExtractionException($"Could not extract text from the PDF: {ex.Message}");
                }
            },
            cancellationToken);

    private static string ExtractPageText(Page page)
    {
        // Layout analysis is heuristic, so a page with degenerate geometry can throw or segment away
        // every block. Either way, falling back to the raw content-stream order for that one page
        // beats failing the whole upload or silently storing an empty document.
        try
        {
            var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters);
            var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
            var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks);

            // Blocks are paragraphs, so they are separated by a blank line: the boundary survives into
            // the stored text and gives the fact extractor a usable unit to reason over.
            var pageText = string.Join("\n\n", ordered.Select(block => block.Text));
            return string.IsNullOrWhiteSpace(pageText) ? page.Text : pageText;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return page.Text;
        }
    }
}
