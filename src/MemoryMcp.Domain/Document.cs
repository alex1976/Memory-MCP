namespace MemoryMcp.Domain;

public sealed class Document
{
    public Guid Id { get; private set; }
    public Guid SpaceId { get; private set; }
    public string Title { get; private set; } = default!;
    public string DocType { get; private set; } = default!;
    public DocumentStatus Status { get; private set; }
    public string? Summary { get; private set; }
    public string? RawContent { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Document()
    {
    }

    public Document(Guid spaceId, string title, string docType, string? rawContent = null, string? summary = null)
    {
        Id = Guid.NewGuid();
        SpaceId = spaceId;
        Title = title;
        DocType = docType;
        RawContent = rawContent;
        Summary = summary;
        Status = DocumentStatus.Pending;
        var now = DateTimeOffset.UtcNow;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public void MarkProcessed(string? summary)
    {
        Status = DocumentStatus.Processed;
        Summary = summary;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void MarkFailed()
    {
        Status = DocumentStatus.Failed;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
