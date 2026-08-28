using MemoryMcp.Application.Abstractions;
using MemoryMcp.Domain;

namespace MemoryMcp.Api.Tests.TestSupport;

/// <summary>
/// Deterministic fact extractor for E2E tests: always emits exactly one fact (the content,
/// unchanged) and relates it to every supplied candidate via "Extends" with a fixed rationale, so a
/// second add_memory call always produces an annotated graph edge to prior memories without needing
/// a real LLM.
/// </summary>
internal sealed class FakeFactExtractor : IFactExtractor
{
    internal const string RelationNote = "fake extractor: relates every fact to every candidate";

    public Task<IReadOnlyList<ExtractedFact>> ExtractAsync(
        string content, IReadOnlyList<MemoryCandidateDto> relatedCandidates, CancellationToken cancellationToken = default)
    {
        var relations = relatedCandidates.Select(c => new ExtractedRelation(c.Id, RelationType.Extends, RelationNote)).ToList();
        IReadOnlyList<ExtractedFact> facts = [new ExtractedFact(content, Category: null, relations)];
        return Task.FromResult(facts);
    }
}
