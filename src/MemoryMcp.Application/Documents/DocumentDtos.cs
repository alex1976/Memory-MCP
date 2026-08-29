namespace MemoryMcp.Application.Documents;

/// <summary><paramref name="CreatedBy"/>/<paramref name="UpdatedBy"/> are display names resolved from the
/// corresponding ids, and are null for documents stored before authorship was recorded.</summary>
public sealed record DocumentSummaryDto(
    Guid Id,
    string Title,
    string DocType,
    string Status,
    string? Summary,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid? CreatedByUserId = null,
    string? CreatedBy = null,
    Guid? UpdatedByUserId = null,
    string? UpdatedBy = null);

public sealed record DocumentDetailDto(
    Guid Id,
    string Title,
    string DocType,
    string Status,
    string? Summary,
    string? RawContent,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid? CreatedByUserId = null,
    string? CreatedBy = null,
    Guid? UpdatedByUserId = null,
    string? UpdatedBy = null);
