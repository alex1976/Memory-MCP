using MemoryMcp.Application.Abstractions;

namespace MemoryMcp.Application.Documents;

public interface IDocumentService
{
    Task<PagedResult<DocumentSummaryDto>> ListDocumentsAsync(
        string? containerTag, int page, int limit, CancellationToken cancellationToken = default);

    Task<DocumentDetailDto> GetDocumentAsync(Guid documentId, CancellationToken cancellationToken = default);
}
