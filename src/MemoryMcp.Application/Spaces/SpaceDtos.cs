namespace MemoryMcp.Application.Spaces;

public sealed record SpaceSummaryDto(Guid Id, string Key, string Name, string AccessLevel, bool IsDefault, int DocumentCount, int MemoryCount);

public sealed record WhoAmIResult(Guid ApiKeyId, string? Label, string? ActiveSpaceKey, IReadOnlyList<SpaceSummaryDto> Spaces);
