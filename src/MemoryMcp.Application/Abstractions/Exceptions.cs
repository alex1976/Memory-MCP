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

/// <summary>Thrown when caller-supplied arguments are unusable (missing, contradictory, out of range).
/// Unlike a bare <see cref="ArgumentException"/>, this is mapped by <c>McpExecution</c>, so the message
/// reaches the client as an actionable tool error instead of an opaque internal failure — which matters
/// because services are entered from prompts and resources too, not just the tool layer.</summary>
public sealed class ValidationException : Exception
{
    public ValidationException(string message) : base(message)
    {
    }
}

/// <summary>Thrown by <see cref="IFactExtractor"/> implementations when no provider is configured, so
/// <see cref="MemoryMcp.Application.Memories.MemoryService"/> can fall back to saving whole content as a single memory.</summary>
public sealed class ExtractorNotConfiguredException : Exception
{
    public ExtractorNotConfiguredException(string message) : base(message)
    {
    }
}

/// <summary>Thrown when binary document content (e.g. a PDF) can't be decoded or its text extracted —
/// surfaced as a tool error (not an unhandled exception) the same way <see cref="SpaceNotFoundException"/> is.</summary>
public sealed class DocumentExtractionException : Exception
{
    public DocumentExtractionException(string message) : base(message)
    {
    }
}
