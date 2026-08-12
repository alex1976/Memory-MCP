using MemoryMcp.Application.Abstractions;

namespace MemoryMcp.Application.Memories;

public interface IMemoryService
{
    Task<SearchMemoryResult> SearchMemoryAsync(
        string? query,
        string? keyword,
        string? category,
        bool includeProfile,
        string? containerTag,
        CancellationToken cancellationToken = default);

    Task<AddMemoryResult> AddMemoryAsync(
        string content, MemoryAction action, string? category, string? containerTag, CancellationToken cancellationToken = default);

    Task<PagedResult<MemorySummaryDto>> ListMemoriesAsync(
        string? containerTag, int page, int limit, CancellationToken cancellationToken = default);

    /// <summary>The same "recent active memories" profile context attached to search results when
    /// <c>includeProfile</c> is set, exposed on its own for callers (e.g. the <c>memory-mcp://profile</c>
    /// resource) that want it without also running a search.</summary>
    Task<IReadOnlyList<MemorySummaryDto>> GetProfileAsync(
        string? containerTag, CancellationToken cancellationToken = default);
}
