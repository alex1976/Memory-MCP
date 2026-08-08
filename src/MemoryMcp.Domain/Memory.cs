namespace MemoryMcp.Domain;

public sealed class Memory
{
    public Guid Id { get; private set; }
    public Guid SpaceId { get; private set; }
    public Guid? DocumentId { get; private set; }
    public string Text { get; private set; } = default!;
    public string? Category { get; private set; }
    public float[]? Embedding { get; private set; }
    public int Version { get; private set; }
    public bool IsActive { get; private set; }
    public Guid? SupersededBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Memory()
    {
    }

    public Memory(Guid spaceId, string text, float[]? embedding, Guid? documentId = null, string? category = null)
    {
        Id = Guid.NewGuid();
        SpaceId = spaceId;
        DocumentId = documentId;
        Text = text;
        Category = category;
        Embedding = embedding;
        Version = 1;
        IsActive = true;
        var now = DateTimeOffset.UtcNow;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public void Forget(Guid? supersededBy = null)
    {
        IsActive = false;
        SupersededBy = supersededBy;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
