using MemoryMcp.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MemoryMcp.Infrastructure.Persistence.Configurations;

public sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> builder)
    {
        builder.ToTable("api_keys");
        builder.HasKey(k => k.Id);

        builder.Property(k => k.KeyHash).HasMaxLength(200).IsRequired();
        builder.HasIndex(k => k.KeyHash).IsUnique();

        builder.Property(k => k.KeyPrefix).HasMaxLength(12).IsRequired();
        builder.Property(k => k.Label).HasMaxLength(200);
        builder.Property(k => k.OwnerEmail).HasMaxLength(320);
    }
}
