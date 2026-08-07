using MemoryMcp.Application.Abstractions;
using MemoryMcp.Infrastructure.Persistence;

namespace MemoryMcp.Api.Tests.TestSupport;

/// <summary>Deterministic embedding stand-in so E2E tests don't need a real OpenAI/Azure OpenAI key.</summary>
internal sealed class FakeEmbeddingProvider : IEmbeddingProvider
{
    public int Dimensions => VectorSettings.Dimensions;

    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        var vector = new float[Dimensions];
        vector[0] = (text.GetHashCode(StringComparison.Ordinal) % 1000) / 1000f;
        return Task.FromResult(vector);
    }

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
