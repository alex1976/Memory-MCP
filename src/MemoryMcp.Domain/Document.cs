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

    /// <summary>Who uploaded or saved the source. Nullable only for rows written before users existed.</summary>
    public Guid? CreatedByUserId { get; private set; }

    /// <summary>Whoever last changed the row — processing status, summary.</summary>
    public Guid? UpdatedByUserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Document()
    {
    }

    public Document(
        Guid spaceId,
        string title,
        string docType,
        string? rawContent = null,
        string? summary = null,
        Guid? createdByUserId = null)
    {
        Id = Guid.NewGuid();
        SpaceId = spaceId;
        Title = title;
        DocType = docType;
        RawContent = rawContent;
        Summary = summary;
        Status = DocumentStatus.Pending;
        CreatedByUserId = createdByUserId;
        UpdatedByUserId = createdByUserId;
        var now = DateTimeOffset.UtcNow;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public void MarkProcessed(string? summary, Guid? byUserId = null)
    {
        Status = DocumentStatus.Processed;
        Summary = summary;
        Touch(byUserId);
    }

    public void MarkFailed(Guid? byUserId = null)
    {
        Status = DocumentStatus.Failed;
        Touch(byUserId);
    }

    private void Touch(Guid? byUserId)
    {
        UpdatedByUserId = byUserId ?? UpdatedByUserId;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
