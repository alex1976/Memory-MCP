using MemoryMcp.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;

namespace MemoryMcp.Infrastructure.Persistence.Configurations;

public sealed class MemoryConfiguration : IEntityTypeConfiguration<Domain.Memory>
{
    public void Configure(EntityTypeBuilder<Domain.Memory> builder)
    {
        builder.ToTable("memories");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Text).IsRequired();
        builder.Property(m => m.Category).HasMaxLength(100);

        // Stored as pgvector's halfvec, not vector: HNSW/IVFFlat indexes cap the `vector` type at 2000
        // dimensions, and our embeddings are 3072 wide (Embeddings:Dimensions), so a plain vector(3072)
        // column would take the index creation down with it. halfvec indexes up to 4000 dimensions and
        // halves storage (2 bytes per component); the half-precision rounding is immaterial for cosine
        // ranking. Similarity search runs in the database now — see MemoryRepository.SearchAsync.
        //
        // The Domain deliberately keeps float[]: Pgvector.HalfVector is a persistence concern, so the
        // conversion lives here. It only runs on write and on the rare entity load, never in the search
        // hot path, which projects ids and distances in SQL without materializing embeddings.
        var embeddingConverter = new ValueConverter<float[]?, HalfVector?>(
            v => v == null ? null : new HalfVector(Array.ConvertAll(v, f => (Half)f)),
            v => v == null ? null : Array.ConvertAll(v.ToArray(), h => (float)h));

        var embeddingComparer = new ValueComparer<float[]?>(
            (a, b) => a == b || (a != null && b != null && a.SequenceEqual(b)),
            v => v == null ? 0 : v.Aggregate(17, (hash, f) => HashCode.Combine(hash, f)),
            v => v == null ? null : v.ToArray());

        builder.Property(m => m.Embedding)
            .HasColumnType($"halfvec({VectorSettings.Dimensions})")
            .HasConversion(embeddingConverter);
        builder.Property(m => m.Embedding).Metadata.SetValueComparer(embeddingComparer);

        builder.HasIndex(m => m.SpaceId);
        builder.HasIndex(m => new { m.SpaceId, m.Category });

        // HNSW index backing the `<=>` cosine ordering in MemoryRepository.SearchAsync. Declared on the
        // model (rather than as raw SQL in a migration) so the snapshot knows it exists and later
        // migrations don't silently drop it. halfvec_cosine_ops is the halfvec counterpart of
        // vector_cosine_ops — the operator class has to match the column type.
        builder.HasIndex(m => m.Embedding)
            .HasMethod("hnsw")
            .HasOperators("halfvec_cosine_ops");

        // GIN trigram index backs both the existing ILIKE substring search and the fuzzy
        // (typo-tolerant) trigram similarity search in MemoryRepository.SearchByKeywordAsync.
        builder.HasIndex(m => m.Text)
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops");

        builder.HasOne<Space>().WithMany().HasForeignKey(m => m.SpaceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Document>().WithMany().HasForeignKey(m => m.DocumentId).OnDelete(DeleteBehavior.SetNull);

        // SetNull, emphatically not Cascade: deleting a user must never delete the knowledge they
        // contributed to a shared space. Losing the author's name is acceptable; losing the team's
        // memories because someone left is not.
        builder.HasOne<User>().WithMany().HasForeignKey(m => m.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<User>().WithMany().HasForeignKey(m => m.UpdatedByUserId).OnDelete(DeleteBehavior.SetNull);
    }
}
