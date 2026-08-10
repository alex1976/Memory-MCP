using MemoryMcp.Infrastructure.Persistence;

namespace MemoryMcp.Infrastructure.Embeddings;

public sealed class EmbeddingOptions
{
    public const string SectionName = "Embeddings";

    /// <summary>"OpenAI", "AzureOpenAI", or "Gemini" (Google's OpenAI-compatible endpoint).</summary>
    public string Provider { get; set; } = "OpenAI";

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Required when Provider is "AzureOpenAI". Optional override for "Gemini" (defaults to Google's OpenAI-compatible base URL).</summary>
    public string? Endpoint { get; set; }

    /// <summary>Model name (OpenAI), deployment name (AzureOpenAI), or embedding model name (Gemini, e.g. "gemini-embedding-001").</summary>
    public string Model { get; set; } = "text-embedding-3-small";

    /// <summary>
    /// Requested embedding width, forwarded as the OpenAI "dimensions" request parameter. All memories in a
    /// space must share the same width, since similarity is computed in-app without pgvector (see
    /// VectorSettings) — change this only together with re-embedding existing data. Gemini's
    /// gemini-embedding-001 defaults to 3072 and needs manual normalization for truncated widths, so prefer
    /// setting this to 3072 (native) rather than truncating when Provider is "Gemini".
    /// </summary>
    public int Dimensions { get; set; } = VectorSettings.Dimensions;
}
