using MemoryMcp.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MemoryMcp.Infrastructure.Persistence.Configurations;

public sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.ToTable("documents");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.Title).HasMaxLength(300).IsRequired();
        builder.Property(d => d.DocType).HasMaxLength(50).IsRequired();
        builder.Property(d => d.Status).HasConversion<short>();

        builder.HasIndex(d => d.SpaceId);
        builder.HasOne<Space>().WithMany().HasForeignKey(d => d.SpaceId).OnDelete(DeleteBehavior.Cascade);
    }
}
