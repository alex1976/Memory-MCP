namespace MemoryMcp.Application.Abstractions;

public interface IEmbeddingProvider
{
    int Dimensions { get; }

    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
}
