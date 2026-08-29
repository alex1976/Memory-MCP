using MemoryMcp.Application.Abstractions;
using MemoryMcp.Domain;
using Microsoft.EntityFrameworkCore;

namespace MemoryMcp.Infrastructure.Persistence.Repositories;

public sealed class UserRepository(MemoryDbContext dbContext) : IUserRepository
{
    public async Task<UserSummary?> FindByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        var normalized = User.NormalizeEmail(email);
        return await dbContext.Users
            .AsNoTracking()
            .Where(u => u.Email == normalized)
            .Select(u => new UserSummary(u.Id, u.Email, u.DisplayName, u.Role))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UserSummary>> GetByIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0)
        {
            return [];
        }

        // Deactivated users are still returned: their name is needed to attribute what they wrote while
        // active, and hiding it would silently turn old memories into anonymous ones.
        return await dbContext.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new UserSummary(u.Id, u.Email, u.DisplayName, u.Role))
            .ToListAsync(cancellationToken);
    }
}
