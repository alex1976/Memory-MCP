namespace MemoryMcp.Application.Abstractions;

/// <summary>Extracts plain text from a PDF's raw bytes, so <c>create_document</c> can store a searchable
/// <see cref="Domain.Document.RawContent"/> instead of the binary payload. Pluggable the same way
/// <see cref="IEmbeddingProvider"/>/<see cref="IFactExtractor"/> are, but this one needs no external
/// service/API key — the PDF is parsed entirely in-process.</summary>
public interface IPdfTextExtractor
{
    /// <exception cref="DocumentExtractionException">The bytes aren't a readable PDF (corrupt, encrypted, or malformed).</exception>
    Task<string> ExtractTextAsync(byte[] pdfBytes, CancellationToken cancellationToken = default);
}
