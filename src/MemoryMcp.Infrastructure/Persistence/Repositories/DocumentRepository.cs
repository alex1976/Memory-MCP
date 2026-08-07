using MemoryMcp.Application.Abstractions;
using MemoryMcp.Domain;
using Microsoft.EntityFrameworkCore;

namespace MemoryMcp.Infrastructure.Persistence.Repositories;

public sealed class DocumentRepository(MemoryDbContext dbContext) : IDocumentRepository
{
    public void Add(Document document) => dbContext.Documents.Add(document);

    public Task<Document?> GetByIdAsync(Guid documentId, CancellationToken cancellationToken = default) =>
        dbContext.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);

    public async Task<(IReadOnlyList<Document> Items, int TotalCount)> ListAsync(
        Guid spaceId, int page, int limit, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Documents.AsNoTracking().Where(d => d.SpaceId == spaceId);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(d => d.UpdatedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }
}
