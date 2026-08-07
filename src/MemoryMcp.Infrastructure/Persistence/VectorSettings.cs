namespace MemoryMcp.Infrastructure.Persistence;

/// <summary>
/// Embeddings are stored as a plain Postgres real[] column (no pgvector extension available in this
/// environment) and compared in-app, so mixing dimensions per space would make similarity scores
/// meaningless. Every IEmbeddingProvider implementation must produce vectors of this length.
/// </summary>
public static class VectorSettings
{
    public const int Dimensions = 1536;
}
