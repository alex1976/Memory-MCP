namespace MemoryMcp.Application.Documents;

public sealed record DocumentSummaryDto(
    Guid Id,
    string Title,
    string DocType,
    string Status,
    string? Summary,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record DocumentDetailDto(
    Guid Id,
    string Title,
    string DocType,
    string Status,
    string? Summary,
    string? RawContent,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
