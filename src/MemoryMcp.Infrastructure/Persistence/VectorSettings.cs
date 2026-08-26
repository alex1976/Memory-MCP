namespace MemoryMcp.Infrastructure.Persistence;

/// <summary>
/// The one authoritative embedding width. Embeddings live in a fixed-width pgvector <c>halfvec</c>
/// column, and EF migrations are generated at design time, so this has to be a compile-time constant
/// rather than configuration: changing it is a schema migration plus a re-embed of every stored memory,
/// never just an appsettings edit.
/// <para>
/// <c>Embeddings:Dimensions</c> must equal this value; <see cref="EmbeddingOptionsValidator"/> enforces
/// that at startup. Without that check the provider can emit vectors the column cannot store, which is
/// how a space ends up with mixed 1536/3072 embeddings and silently meaningless similarity scores.
/// </para>
/// </summary>
public static class VectorSettings
{
    public const int Dimensions = 3072;
}
