namespace MemoryMcp.Application.Abstractions;

public static class Paging
{
    public const int DefaultLimit = 10;
    public const int MaxLimit = 50;

    public static (int Page, int Limit) Clamp(int page, int limit)
    {
        var clampedPage = page < 1 ? 1 : page;
        var clampedLimit = limit <= 0 ? DefaultLimit : Math.Min(limit, MaxLimit);
        return (clampedPage, clampedLimit);
    }
}
