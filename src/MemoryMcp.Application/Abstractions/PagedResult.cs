namespace MemoryMcp.Application.Abstractions;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int Limit, int TotalCount);
