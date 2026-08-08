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
}
