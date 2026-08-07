namespace MemoryMcp.Api.Tests;

[CollectionDefinition(Name)]
public sealed class McpApiCollection : ICollectionFixture<McpApiFactory>
{
    public const string Name = "Mcp Api collection";
}
