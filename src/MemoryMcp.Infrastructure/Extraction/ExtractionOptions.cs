namespace MemoryMcp.Infrastructure.Extraction;

public sealed class ExtractionOptions
{
    public const string SectionName = "Extraction";

    /// <summary>"OpenAI", "AzureOpenAI", or "Gemini" (Google's OpenAI-compatible chat endpoint). Left unconfigured
    /// (empty ApiKey), add_memory falls back to saving whole content as a single memory with no graph edges.</summary>
    public string Provider { get; set; } = "OpenAI";

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Required when Provider is "AzureOpenAI". Optional override for "Gemini" or a self-hosted
    /// OpenAI-compatible endpoint (Ollama, vLLM, LM Studio).</summary>
    public string? Endpoint { get; set; }

    /// <summary>Model name (OpenAI/Gemini) or deployment name (AzureOpenAI).</summary>
    public string Model { get; set; } = "gpt-4o-mini";
}
