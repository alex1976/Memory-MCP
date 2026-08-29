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

        // Cascade: deleting a user takes their credentials with them, so a hard delete can't leave keys
        // that authenticate to a principal that no longer exists. Deactivating the user (the reversible
        // path) is what offboarding should normally use.
        builder.HasIndex(k => k.UserId);
        builder.HasOne<User>().WithMany().HasForeignKey(k => k.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
