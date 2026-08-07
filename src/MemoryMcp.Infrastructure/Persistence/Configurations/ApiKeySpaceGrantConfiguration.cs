using MemoryMcp.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MemoryMcp.Infrastructure.Persistence.Configurations;

public sealed class ApiKeySpaceGrantConfiguration : IEntityTypeConfiguration<ApiKeySpaceGrant>
{
    public void Configure(EntityTypeBuilder<ApiKeySpaceGrant> builder)
    {
        builder.ToTable("api_key_space_grants");
        builder.HasKey(g => g.Id);

        builder.HasIndex(g => new { g.ApiKeyId, g.SpaceId }).IsUnique();

        builder.HasOne<ApiKey>().WithMany().HasForeignKey(g => g.ApiKeyId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Space>().WithMany().HasForeignKey(g => g.SpaceId).OnDelete(DeleteBehavior.Cascade);
    }
}
