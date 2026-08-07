using MemoryMcp.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MemoryMcp.Infrastructure.Persistence.Configurations;

public sealed class MemoryConfiguration : IEntityTypeConfiguration<Domain.Memory>
{
    public void Configure(EntityTypeBuilder<Domain.Memory> builder)
    {
        builder.ToTable("memories");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Text).IsRequired();

        // Plain Postgres real[] via Npgsql's native array mapping (no pgvector extension available
        // in this environment); similarity search is computed in-app — see MemoryRepository.SearchAsync.
        var embeddingComparer = new ValueComparer<float[]?>(
            (a, b) => a == b || (a != null && b != null && a.SequenceEqual(b)),
            v => v == null ? 0 : v.Aggregate(17, (hash, f) => HashCode.Combine(hash, f)),
            v => v == null ? null : v.ToArray());

        builder.Property(m => m.Embedding).Metadata.SetValueComparer(embeddingComparer);

        builder.HasIndex(m => m.SpaceId);

        builder.HasOne<Space>().WithMany().HasForeignKey(m => m.SpaceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Document>().WithMany().HasForeignKey(m => m.DocumentId).OnDelete(DeleteBehavior.SetNull);
    }
}
