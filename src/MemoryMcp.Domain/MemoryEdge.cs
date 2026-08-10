namespace MemoryMcp.Domain;

/// <summary>
/// A typed, directed link between two memories. <see cref="FromMemoryId"/> acts on
/// <see cref="ToMemoryId"/> — e.g. <see cref="RelationType.Updates"/> means the "from" memory
/// supersedes the "to" memory. Edges are immutable once created; there is no "forget an edge"
/// operation, since forgetting a memory (<see cref="Memory.Forget"/>) already covers the relevant case.
/// </summary>
public sealed class MemoryEdge
{
    public Guid Id { get; private set; }
    public Guid SpaceId { get; private set; }
    public Guid FromMemoryId { get; private set; }
    public Guid ToMemoryId { get; private set; }
    public RelationType RelationType { get; private set; }
    public string? Note { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private MemoryEdge()
    {
    }

    public MemoryEdge(Guid spaceId, Guid fromMemoryId, Guid toMemoryId, RelationType relationType, string? note = null)
    {
        Id = Guid.NewGuid();
        SpaceId = spaceId;
        FromMemoryId = fromMemoryId;
        ToMemoryId = toMemoryId;
        RelationType = relationType;
        Note = note;
        CreatedAt = DateTimeOffset.UtcNow;
    }
}
