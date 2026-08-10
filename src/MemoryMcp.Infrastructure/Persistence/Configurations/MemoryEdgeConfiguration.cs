using MemoryMcp.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MemoryMcp.Infrastructure.Persistence.Configurations;

public sealed class MemoryEdgeConfiguration : IEntityTypeConfiguration<MemoryEdge>
{
    public void Configure(EntityTypeBuilder<MemoryEdge> builder)
    {
        builder.ToTable("memory_edges");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Note).HasMaxLength(500);

        // Cheap neighbor lookups in either direction for the recursive-CTE traversal in MemoryEdgeRepository.
        builder.HasIndex(e => new { e.SpaceId, e.FromMemoryId });
        builder.HasIndex(e => new { e.SpaceId, e.ToMemoryId });

        builder.HasOne<Space>().WithMany().HasForeignKey(e => e.SpaceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Memory>().WithMany().HasForeignKey(e => e.FromMemoryId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Memory>().WithMany().HasForeignKey(e => e.ToMemoryId).OnDelete(DeleteBehavior.Cascade);
    }
}
