using MemoryMcp.Domain;

namespace MemoryMcp.Application.Abstractions;

public interface IDocumentRepository
{
    void Add(Document document);

    Task<Document?> GetByIdAsync(Guid documentId, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<Document> Items, int TotalCount)> ListAsync(
        Guid spaceId, int page, int limit, CancellationToken cancellationToken = default);
}
