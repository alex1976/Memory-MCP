namespace MemoryMcp.Domain;

/// <summary>
/// A typed, directed link between two memories. <see cref="FromMemoryId"/> acts on
/// <see cref="ToMemoryId"/> — e.g. <see cref="RelationType.Updates"/> means the "from" memory
/// supersedes the "to" memory. Edges are immutable once created; there is no "forget an edge"
/// operation, since forgetting a memory (<see cref="Memory.Forget"/>) already covers the relevant case.
/// </summary>
public sealed class MemoryEdge
{
    /// <summary>Column width of <see cref="Note"/>. The constructor clamps to it rather than rejecting:
    /// the note comes from an LLM that can't be held to a length, and an over-long rationale must not
    /// fail the surrounding add_memory save (which persists memories and edges in one transaction).</summary>
    public const int NoteMaxLength = 500;

    public Guid Id { get; private set; }
    public Guid SpaceId { get; private set; }
    public Guid FromMemoryId { get; private set; }
    public Guid ToMemoryId { get; private set; }
    public RelationType RelationType { get; private set; }

    /// <summary>Human-readable rationale for why this edge exists — why the extractor classified the
    /// relation as it did. Audit/debugging aid (most valuable for an <see cref="RelationType.Updates"/>
    /// that deactivated a memory, and for <see cref="RelationType.Derives"/>, where the combined
    /// memories aren't otherwise visible), not queryable data: it's the model's own justification,
    /// unindexed and free-form. Null when extraction supplied none.</summary>
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
        Note = NormalizeNote(note);
        CreatedAt = DateTimeOffset.UtcNow;
    }

    private static string? NormalizeNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return null;
        }

        var trimmed = note.Trim();
        return trimmed.Length <= NoteMaxLength
            ? trimmed
            : trimmed[..(NoteMaxLength - 1)] + "…";
    }
}
