namespace MemoryMcp.Application.Spaces;

/// <summary><paramref name="AccessLevel"/> is the effective level — the space grant already capped by the
/// caller's role — so a Reader never sees "ReadWrite" against a space they cannot write to.</summary>
public sealed record SpaceSummaryDto(Guid Id, string Key, string Name, string AccessLevel, bool IsDefault, int DocumentCount, int MemoryCount);

public sealed record WhoAmIResult(
    Guid ApiKeyId,
    string? Label,
    Guid UserId,
    string UserEmail,
    string UserDisplayName,
    string UserRole,
    string? ActiveSpaceKey,
    IReadOnlyList<SpaceSummaryDto> Spaces);
