using MemoryMcp.Domain;
using Microsoft.EntityFrameworkCore;

namespace MemoryMcp.Infrastructure.Persistence;

public sealed class MemoryDbContext(DbContextOptions<MemoryDbContext> options) : DbContext(options)
{
    public DbSet<Space> Spaces => Set<Space>();
    public DbSet<User> Users => Set<User>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<ApiKeySpaceGrant> ApiKeySpaceGrants => Set<ApiKeySpaceGrant>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<Memory> Memories => Set<Memory>();
    public DbSet<MemoryEdge> MemoryEdges => Set<MemoryEdge>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("pg_trgm");
        // Required by the halfvec column and HNSW index on memories (see MemoryConfiguration). Creating
        // it needs superuser on the target database, since pgvector isn't a trusted extension — worth
        // checking on managed Postgres before a first deploy.
        modelBuilder.HasPostgresExtension("vector");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MemoryDbContext).Assembly);
    }
}
