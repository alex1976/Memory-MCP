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

    /// <summary>Author of the memory. Nullable only because rows written before users existed have no
    /// author to name; everything created through <c>MemoryService</c> carries one.</summary>
    public Guid? CreatedByUserId { get; private set; }

    /// <summary>Whoever last changed the row — in practice whoever forgot or superseded it, since a
    /// memory's text is never edited in place. In a shared space this is the only record of which
    /// member deactivated a colleague's memory.</summary>
    public Guid? UpdatedByUserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Memory()
    {
    }

    public Memory(
        Guid spaceId,
        string text,
        float[]? embedding,
        Guid? documentId = null,
        string? category = null,
        Guid? createdByUserId = null)
    {
        Id = Guid.NewGuid();
        SpaceId = spaceId;
        DocumentId = documentId;
        Text = text;
        Category = category;
        Embedding = embedding;
        Version = 1;
        IsActive = true;
        CreatedByUserId = createdByUserId;
        UpdatedByUserId = createdByUserId;
        var now = DateTimeOffset.UtcNow;
        CreatedAt = now;
        UpdatedAt = now;
    }

    /// <summary><paramref name="byUserId"/> is recorded on <see cref="UpdatedByUserId"/> so a deactivation
    /// is attributable to the member who caused it, whether it came from an explicit forget or from
    /// another member's save superseding this memory.</summary>
    public void Forget(Guid? byUserId = null, Guid? supersededBy = null)
    {
        IsActive = false;
        SupersededBy = supersededBy;
        UpdatedByUserId = byUserId ?? UpdatedByUserId;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
