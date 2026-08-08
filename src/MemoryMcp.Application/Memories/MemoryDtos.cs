namespace MemoryMcp.Application.Memories;

public enum MemoryAction
{
    Save,
    Forget,
}

public sealed record MemorySummaryDto(Guid Id, string Text, int Version, Guid? DocumentId, bool IsActive, DateTimeOffset CreatedAt, string? Category);

public sealed record MemorySearchResultDto(Guid Id, string Text, double Score, Guid? DocumentId, string? Category);

public sealed record SearchMemoryResult(IReadOnlyList<MemorySearchResultDto> Matches, IReadOnlyList<MemorySummaryDto>? Profile);

public sealed record AddMemoryResult(Guid? MemoryId, MemoryAction Action, int AffectedCount, string Message);
