using MemoryMcp.Application.Abstractions;
using MemoryMcp.Domain;

namespace MemoryMcp.Application.Memories;

public enum MemoryAction
{
    Save,
    Forget,
}

public sealed record MemorySummaryDto(Guid Id, string Text, int Version, Guid? DocumentId, bool IsActive, DateTimeOffset CreatedAt, string? Category);

public sealed record RelatedMemoryDto(Guid Id, string Text, RelationType RelationType, int Hops, bool IsActive = true, RelatedMemoryDirection Direction = RelatedMemoryDirection.Outgoing);

public sealed record MemorySearchResultDto(
    Guid Id, string Text, double Score, Guid? DocumentId, string? Category, IReadOnlyList<RelatedMemoryDto>? RelatedMemories = null);

public sealed record SearchMemoryResult(IReadOnlyList<MemorySearchResultDto> Matches, IReadOnlyList<MemorySummaryDto>? Profile);

public sealed record AddMemoryResult(Guid? MemoryId, MemoryAction Action, int AffectedCount, string Message, IReadOnlyList<Guid>? MemoryIds = null);
