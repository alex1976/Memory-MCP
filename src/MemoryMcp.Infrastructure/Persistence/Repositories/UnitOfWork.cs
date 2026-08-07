using MemoryMcp.Application.Abstractions;

namespace MemoryMcp.Infrastructure.Persistence.Repositories;

public sealed class UnitOfWork(MemoryDbContext dbContext) : IUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        dbContext.SaveChangesAsync(cancellationToken);
}
