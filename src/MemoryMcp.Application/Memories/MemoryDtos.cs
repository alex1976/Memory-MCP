using MemoryMcp.Application.Abstractions;
using MemoryMcp.Domain;

namespace MemoryMcp.Application.Memories;

public enum MemoryAction
{
    Save,
    Forget,
}

/// <summary><paramref name="CreatedBy"/>/<paramref name="UpdatedBy"/> are display names resolved from the
/// corresponding ids; both are null for memories written before authorship was recorded. In a shared
/// space the pair is what lets a reader tell their own facts from a colleague's, and
/// <paramref name="UpdatedBy"/> names whoever last forgot or superseded the memory.</summary>
public sealed record MemorySummaryDto(
    Guid Id,
    string Text,
    int Version,
    Guid? DocumentId,
    bool IsActive,
    DateTimeOffset CreatedAt,
    string? Category,
    Guid? CreatedByUserId = null,
    string? CreatedBy = null,
    Guid? UpdatedByUserId = null,
    string? UpdatedBy = null);

public sealed record RelatedMemoryDto(
    Guid Id,
    string Text,
    RelationType RelationType,
    int Hops,
    bool IsActive = true,
    RelatedMemoryDirection Direction = RelatedMemoryDirection.Outgoing,
    string? Note = null);

public sealed record MemorySearchResultDto(
    Guid Id,
    string Text,
    double Score,
    Guid? DocumentId,
    string? Category,
    IReadOnlyList<RelatedMemoryDto>? RelatedMemories = null,
    Guid? CreatedByUserId = null,
    string? CreatedBy = null);

public sealed record SearchMemoryResult(IReadOnlyList<MemorySearchResultDto> Matches, IReadOnlyList<MemorySummaryDto>? Profile);

/// <summary><paramref name="ContestedMemoryIds"/> lists other members' memories that a save appears to make
/// outdated but did not deactivate: superseding a colleague's fact needs a human decision, not an LLM's.</summary>
public sealed record AddMemoryResult(
    Guid? MemoryId,
    MemoryAction Action,
    int AffectedCount,
    string Message,
    IReadOnlyList<Guid>? MemoryIds = null,
    IReadOnlyList<Guid>? ContestedMemoryIds = null);

public sealed record GraphNodeDto(Guid Id, string Text, string? Category, bool IsActive, DateTimeOffset CreatedAt);

public sealed record GraphEdgeDto(Guid FromMemoryId, Guid ToMemoryId, RelationType RelationType, string? Note = null);

public sealed record SpaceGraphDto(IReadOnlyList<GraphNodeDto> Nodes, IReadOnlyList<GraphEdgeDto> Edges);
