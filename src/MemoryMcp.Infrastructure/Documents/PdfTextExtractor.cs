using System.Text;
using MemoryMcp.Application.Abstractions;
using UglyToad.PdfPig;

namespace MemoryMcp.Infrastructure.Documents;

/// <summary>
/// Extracts text from a PDF entirely in-process via PdfPig (pure managed, no native dependencies) —
/// unlike <see cref="IEmbeddingProvider"/>/<see cref="IFactExtractor"/>, this needs no external
/// service/API key, so it's always available regardless of configuration.
/// </summary>
public sealed class PdfTextExtractor : IPdfTextExtractor
{
    public Task<string> ExtractTextAsync(byte[] pdfBytes, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                try
                {
                    using var document = PdfDocument.Open(pdfBytes);
                    var text = new StringBuilder();
                    foreach (var page in document.GetPages())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (text.Length > 0)
                        {
                            text.AppendLine();
                        }

                        text.Append(page.Text);
                    }

                    return text.ToString();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new DocumentExtractionException($"Could not extract text from the PDF: {ex.Message}");
                }
            },
            cancellationToken);
}
