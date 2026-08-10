using MemoryMcp.Application.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI.Embeddings;

namespace MemoryMcp.Infrastructure.Embeddings;

/// <summary>
/// Wraps the OpenAI <see cref="EmbeddingClient"/>, which is also returned by
/// <c>AzureOpenAIClient.GetEmbeddingClient</c> and, pointed at a custom endpoint, by Gemini's
/// OpenAI-compatible API — so this single implementation backs the "OpenAI", "AzureOpenAI", and
/// "Gemini" provider options; only client construction differs (see DependencyInjection).
/// </summary>
public sealed class OpenAiCompatibleEmbeddingProvider(Lazy<EmbeddingClient> client, IOptions<EmbeddingOptions> options) : IEmbeddingProvider
{
    public int Dimensions => options.Value.Dimensions;

    private EmbeddingGenerationOptions GenerationOptions => new() { Dimensions = Dimensions };

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
