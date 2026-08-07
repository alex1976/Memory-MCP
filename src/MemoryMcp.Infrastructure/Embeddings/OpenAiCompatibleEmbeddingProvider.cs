using MemoryMcp.Application.Abstractions;
using MemoryMcp.Infrastructure.Persistence;
using OpenAI.Embeddings;

namespace MemoryMcp.Infrastructure.Embeddings;

/// <summary>
/// Wraps the OpenAI <see cref="EmbeddingClient"/>, which is also returned by
/// <c>AzureOpenAIClient.GetEmbeddingClient</c> — so this single implementation backs both
/// the "OpenAI" and "AzureOpenAI" provider options; only client construction differs (see DependencyInjection).
/// </summary>
public sealed class OpenAiCompatibleEmbeddingProvider(Lazy<EmbeddingClient> client) : IEmbeddingProvider
{
    private static readonly EmbeddingGenerationOptions GenerationOptions = new() { Dimensions = VectorSettings.Dimensions };

    public int Dimensions => VectorSettings.Dimensions;

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        var result = await client.Value.GenerateEmbeddingAsync(text, GenerationOptions, cancellationToken);
        return result.Value.ToFloats().ToArray();
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        var result = await client.Value.GenerateEmbeddingsAsync(texts, GenerationOptions, cancellationToken);
        return result.Value.Select(e => e.ToFloats().ToArray()).ToList();
    }
}
