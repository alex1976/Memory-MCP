using Microsoft.EntityFrameworkCore;

namespace MemoryMcp.Infrastructure.Persistence;

/// <summary>
/// The single place that configures the Npgsql provider for <see cref="MemoryDbContext"/>. Anything that
/// builds the context — the DI registration, integration tests, design-time tooling — must go through
/// here, because omitting <c>UseVector()</c> makes the model fail validation outright: the halfvec column
/// behind <c>Memory.Embedding</c> has no mapping without pgvector's type handlers.
/// </summary>
public static class MemoryDbContextOptions
{
    public static DbContextOptionsBuilder UseMemoryMcpNpgsql(
        this DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString, npgsql => npgsql.UseVector());

    /// <summary>Generic overload, so callers that build a typed options object (integration tests) keep
    /// the <typeparamref name="TContext"/> in the result instead of widening to the base builder.</summary>
    public static DbContextOptionsBuilder<TContext> UseMemoryMcpNpgsql<TContext>(
        this DbContextOptionsBuilder<TContext> builder, string connectionString)
        where TContext : DbContext =>
        builder.UseNpgsql(connectionString, npgsql => npgsql.UseVector());
}
