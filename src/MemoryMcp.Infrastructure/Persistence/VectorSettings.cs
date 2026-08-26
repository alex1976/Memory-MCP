namespace MemoryMcp.Infrastructure.Persistence;

/// <summary>
/// Embeddings are stored as a plain Postgres real[] column (no pgvector extension available in this
/// environment) and compared in-app, so mixing widths within a space would make similarity scores
/// meaningless. This is the *default* width only — the effective width is Embeddings:Dimensions
/// (see EmbeddingOptions), which must not be changed without re-embedding existing memories.
/// </summary>
public static class VectorSettings
{
    public const int Dimensions = 1536;
}
