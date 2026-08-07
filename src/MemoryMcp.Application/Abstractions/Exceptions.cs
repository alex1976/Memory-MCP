namespace MemoryMcp.Application.Abstractions;

public sealed class SpaceNotFoundException : Exception
{
    public SpaceNotFoundException(string? containerTag)
        : base(containerTag is null
            ? "No active space is configured for this API key."
            : $"Space '{containerTag}' was not found or is not accessible.")
    {
    }
}

public sealed class AccessDeniedException : Exception
{
    public AccessDeniedException(string message) : base(message)
    {
    }
}

public sealed class EntityNotFoundException : Exception
{
    public EntityNotFoundException(string message) : base(message)
    {
    }
}
