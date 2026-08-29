using MemoryMcp.Application.Abstractions;
using MemoryMcp.Infrastructure.Persistence;

namespace MemoryMcp.Api.Tests.TestSupport;

/// <summary>Deterministic embedding stand-in so E2E tests don't need a real OpenAI/Azure OpenAI key.</summary>
internal sealed class FakeEmbeddingProvider : IEmbeddingProvider
{
    public int Dimensions => VectorSettings.Dimensions;

    /// <summary>Exposed statically so seeded rows written straight to the database (bypassing the tools)
    /// carry the same vectors a save through the API would have produced.</summary>
    public static float[] EmbeddingFor(string text)
    {
        var vector = new float[VectorSettings.Dimensions];
        vector[0] = (text.GetHashCode(StringComparison.Ordinal) % 1000) / 1000f;
        return vector;
    }

    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
        Task.FromResult(EmbeddingFor(text));

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        var results = new List<float[]>(texts.Count);
        foreach (var text in texts)
        {
            results.Add(await EmbedAsync(text, cancellationToken));
        }

        return results;
    }
}
