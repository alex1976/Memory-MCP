namespace MemoryMcp.Infrastructure.Embeddings;

public sealed class EmbeddingOptions
{
    public const string SectionName = "Embeddings";

    /// <summary>"OpenAI" or "AzureOpenAI".</summary>
    public string Provider { get; set; } = "OpenAI";

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Required when Provider is "AzureOpenAI".</summary>
    public string? Endpoint { get; set; }

    /// <summary>Model name (OpenAI) or deployment name (AzureOpenAI).</summary>
    public string Model { get; set; } = "text-embedding-3-small";
}
