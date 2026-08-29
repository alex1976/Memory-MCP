using MemoryMcp.Domain;

namespace MemoryMcp.Application.Abstractions;

public sealed record UserSummary(Guid Id, string Email, string DisplayName, UserRole Role);

public interface IUserRepository
{
    Task<UserSummary?> FindByEmailAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>Batch lookup: results carry author ids, and resolving each one individually would put an
    /// N+1 behind every search and every page of listMemories/listDocuments.</summary>
    Task<IReadOnlyList<UserSummary>> GetByIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default);
}

/// <summary>
/// Author ids resolved to something an LLM can cite. Built once per call from the ids actually present
/// in a result set, then queried per row — <see cref="Empty"/> when a result set has no attributed rows,
/// so the common "nothing to resolve" path costs no query at all.
/// </summary>
public sealed class UserAttribution
{
    public static UserAttribution Empty { get; } = new(new Dictionary<Guid, UserSummary>());

    private readonly IReadOnlyDictionary<Guid, UserSummary> _byId;

    private UserAttribution(IReadOnlyDictionary<Guid, UserSummary> byId) => _byId = byId;

    /// <summary>Loads the users behind <paramref name="userIds"/>, ignoring nulls and duplicates.</summary>
    public static async Task<UserAttribution> LoadAsync(
        IUserRepository userRepository, IEnumerable<Guid?> userIds, CancellationToken cancellationToken = default)
    {
        var ids = userIds.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        if (ids.Count == 0)
        {
            return Empty;
        }

        var users = await userRepository.GetByIdsAsync(ids, cancellationToken);
        return new UserAttribution(users.ToDictionary(u => u.Id));
    }

    /// <summary>Display name of an author, or null when unattributed (pre-users rows) or the user has
    /// since been deleted — never throws, since a missing name must not fail a read.</summary>
    public string? DisplayName(Guid? userId) =>
        userId.HasValue && _byId.TryGetValue(userId.Value, out var user) ? user.DisplayName : null;
}
