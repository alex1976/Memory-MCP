using MemoryMcp.Application.Abstractions;
using MemoryMcp.Domain;

namespace MemoryMcp.Api.Tests.TestSupport;

/// <summary>
/// Deterministic fact extractor for E2E tests: always emits exactly one fact (the content,
/// unchanged) and relates it to every supplied candidate via "Extends", so a second add_memory
/// call always produces a graph edge to prior memories without needing a real LLM.
/// </summary>
internal sealed class FakeFactExtractor : IFactExtractor
{
    public Task<IReadOnlyList<ExtractedFact>> ExtractAsync(
        string content, IReadOnlyList<MemoryCandidateDto> relatedCandidates, CancellationToken cancellationToken = default)
    {
        var relations = relatedCandidates.Select(c => new ExtractedRelation(c.Id, RelationType.Extends)).ToList();
        IReadOnlyList<ExtractedFact> facts = [new ExtractedFact(content, Category: null, relations)];
        return Task.FromResult(facts);
    }
}
