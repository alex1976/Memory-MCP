using MemoryMcp.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MemoryMcp.Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(u => u.Id);

        builder.Property(u => u.Email).HasMaxLength(320).IsRequired();
        builder.HasIndex(u => u.Email).IsUnique();

        builder.Property(u => u.DisplayName).HasMaxLength(200).IsRequired();

        // Stored as text rather than an int: the role set is compared by identity, not order, and a
        // string column lets a future role be added without renumbering existing rows — the migration
        // trap that AccessLevel (compared with >=, stored as int) still has.
        builder.Property(u => u.Role).HasConversion<string>().HasMaxLength(20).IsRequired();
    }
}
