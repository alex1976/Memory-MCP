using MemoryMcp.Domain;

namespace MemoryMcp.Application.Abstractions;

public sealed record MemoryCandidateDto(Guid Id, string Text, string? Category);

public sealed record ExtractedRelation(Guid ExistingMemoryId, RelationType RelationType);

public sealed record ExtractedFact(string Text, string? Category, IReadOnlyList<ExtractedRelation> RelationsToExisting);

/// <summary>
/// Splits saved content into atomic facts and classifies each fact's relation (if any) to a supplied
/// set of existing candidate memories. Mirrors <see cref="IEmbeddingProvider"/>'s shape: a pluggable,
/// optionally-unconfigured provider that callers fall back around (see <see cref="ExtractorNotConfiguredException"/>).
/// </summary>
public interface IFactExtractor
{
    Task<IReadOnlyList<ExtractedFact>> ExtractAsync(
        string content, IReadOnlyList<MemoryCandidateDto> relatedCandidates, CancellationToken cancellationToken = default);
}
