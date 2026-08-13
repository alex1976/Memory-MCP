using MemoryMcp.Application.Abstractions;

namespace MemoryMcp.Application.Documents;

public interface IDocumentService
{
    Task<PagedResult<DocumentSummaryDto>> ListDocumentsAsync(
        string? containerTag, int page, int limit, CancellationToken cancellationToken = default);

    Task<DocumentDetailDto> GetDocumentAsync(Guid documentId, CancellationToken cancellationToken = default);

    /// <summary>Stores <paramref name="content"/> as a new document (source-of-truth storage only — this does
    /// not run fact extraction; save the same content via <c>add_memory</c> separately to also extract memories).
    /// When <paramref name="docType"/> is <c>"pdf"</c> (case-insensitive), <paramref name="content"/> must be the
    /// base64-encoded PDF bytes instead of plain text — the stored/returned content is the extracted text.</summary>
    /// <exception cref="Abstractions.DocumentExtractionException">
    /// <paramref name="docType"/> is <c>"pdf"</c> but <paramref name="content"/> isn't valid base64, or the PDF's text couldn't be extracted.
    /// </exception>
    Task<DocumentSummaryDto> CreateDocumentAsync(
        string title, string docType, string content, string? summary, string? containerTag, CancellationToken cancellationToken = default);
}
