using MemoryMcp.Application.Abstractions;
using MemoryMcp.Domain;

namespace MemoryMcp.Application.Memories;

public sealed class MemoryService(
    IMemoryRepository memoryRepository,
    IDocumentRepository documentRepository,
    IEmbeddingProvider embeddingProvider,
    IMemoryEdgeRepository memoryEdgeRepository,
    IFactExtractor factExtractor,
    IMemoryGraphService memoryGraphService,
    IUnitOfWork unitOfWork,
    ICurrentAccessContext accessContext) : IMemoryService
{
    private const int SearchTopK = 10;
    private const int ProfileTake = 5;
    private const int ForgetCandidateTopK = 3;
    private const double ForgetSimilarityThreshold = 0.8;
    private const int ExtractionCandidateTopK = 5;
    private const int RelatedMemoriesTopMatches = 3;
    private const int RelatedMemoriesMaxHops = 2;

    public async Task<SearchMemoryResult> SearchMemoryAsync(
        string? query,
        string? keyword,
        string? category,
        bool includeProfile,
        string? containerTag,
        CancellationToken cancellationToken = default)
    {
        var grant = RequireAccess(containerTag, AccessLevel.Read);

        List<MemorySearchResultDto> matches;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var embedding = await embeddingProvider.EmbedAsync(query, cancellationToken);
            var hits = await memoryRepository.SearchAsync(grant.SpaceId, embedding, SearchTopK, category, cancellationToken);
            matches = hits.Select(h => new MemorySearchResultDto(h.Memory.Id, h.Memory.Text, h.Score, h.Memory.DocumentId, h.Memory.Category)).ToList();
        }
        else if (!string.IsNullOrWhiteSpace(keyword))
        {
            var hits = await memoryRepository.SearchByKeywordAsync(grant.SpaceId, keyword, SearchTopK, category, cancellationToken);
            matches = hits.Select(m => new MemorySearchResultDto(m.Id, m.Text, Score: 1.0, m.DocumentId, m.Category)).ToList();
        }
        else if (!string.IsNullOrWhiteSpace(category))
        {
            var hits = await memoryRepository.ListByCategoryAsync(grant.SpaceId, category, SearchTopK, cancellationToken);
            matches = hits.Select(m => new MemorySearchResultDto(m.Id, m.Text, Score: 1.0, m.DocumentId, m.Category)).ToList();
        }
        else
        {
            throw new ArgumentException("Provide at least one of query, keyword, or category.");
        }

        for (var i = 0; i < matches.Count && i < RelatedMemoriesTopMatches; i++)
        {
            var related = await memoryGraphService.GetRelatedAsync(matches[i].Id, grant.SpaceId, RelatedMemoriesMaxHops, cancellationToken);
            if (related.Count > 0)
            {
                matches[i] = matches[i] with { RelatedMemories = related };
            }
        }

        var profile = includeProfile ? await GetProfileAsync(grant, cancellationToken) : null;

        return new SearchMemoryResult(matches, profile);
    }

    public async Task<IReadOnlyList<MemorySummaryDto>> GetProfileAsync(
        string? containerTag, CancellationToken cancellationToken = default) =>
        await GetProfileAsync(RequireAccess(containerTag, AccessLevel.Read), cancellationToken);

    private async Task<IReadOnlyList<MemorySummaryDto>> GetProfileAsync(SpaceGrant grant, CancellationToken cancellationToken)
    {
        var recent = await memoryRepository.ListRecentActiveAsync(grant.SpaceId, ProfileTake, cancellationToken);
        return recent.Select(ToSummary).ToList();
    }

    public async Task<AddMemoryResult> AddMemoryAsync(
        string content, MemoryAction action, string? category, string? containerTag, CancellationToken cancellationToken = default)
    {
        var grant = RequireAccess(containerTag, AccessLevel.ReadWrite);

        return action switch
        {
            MemoryAction.Save => await SaveAsync(grant.SpaceId, content, category, cancellationToken),
            MemoryAction.Forget => await ForgetAsync(grant.SpaceId, content, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported memory action."),
        };
    }

    public async Task<PagedResult<MemorySummaryDto>> ListMemoriesAsync(
        string? containerTag, int page, int limit, CancellationToken cancellationToken = default)
    {
        var grant = RequireAccess(containerTag, AccessLevel.Read);

        var (clampedPage, clampedLimit) = Paging.Clamp(page, limit);
        var (items, totalCount) = await memoryRepository.ListAsync(grant.SpaceId, clampedPage, clampedLimit, cancellationToken);

        var dtos = items.Select(ToSummary).ToList();
        return new PagedResult<MemorySummaryDto>(dtos, clampedPage, clampedLimit, totalCount);
    }

    private async Task<AddMemoryResult> SaveAsync(Guid spaceId, string content, string? category, CancellationToken cancellationToken)
    {
        var title = content.Length > 80 ? content[..80] + "…" : content;
        var document = new Document(spaceId, title, docType: "note", rawContent: content);
        document.MarkProcessed(summary: null);
        documentRepository.Add(document);

        var contentEmbedding = await embeddingProvider.EmbedAsync(content, cancellationToken);

        IReadOnlyList<ExtractedFact> facts;
        IReadOnlyDictionary<Guid, MemorySearchHit> candidatesById;
        try
        {
            var candidateHits = await memoryRepository.SearchAsync(spaceId, contentEmbedding, ExtractionCandidateTopK, category, cancellationToken);
            candidatesById = candidateHits.ToDictionary(h => h.Memory.Id);

            var candidates = candidateHits.Select(h => new MemoryCandidateDto(h.Memory.Id, h.Memory.Text, h.Memory.Category)).ToList();
            facts = await factExtractor.ExtractAsync(content, candidates, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Covers both "no extractor configured" and any other extraction failure (LLM outage, rate
            // limit, malformed/refused response): degrade to the pre-graph-memory single-memory save
            // below rather than fail a call that used to always succeed.
            facts = [];
            candidatesById = new Dictionary<Guid, MemorySearchHit>();
        }

        if (facts.Count == 0)
        {
            // Extraction unconfigured/failed (or returned nothing usable): fall back to saving the whole
            // content as a single memory, exactly as before graph memory existed. Zero edges created.
            var memory = new Memory(spaceId, content, contentEmbedding, document.Id, category);
            memoryRepository.Add(memory);

            await unitOfWork.SaveChangesAsync(cancellationToken);

            return new AddMemoryResult(memory.Id, MemoryAction.Save, 1, "Memory saved.", [memory.Id]);
        }

        var factEmbeddings = await EmbedFactTextsAsync(facts, content, contentEmbedding, cancellationToken);

        var memoryIds = new List<Guid>();
        var forgottenCount = 0;
        for (var i = 0; i < facts.Count; i++)
        {
            var fact = facts[i];
            var factMemory = new Memory(spaceId, fact.Text, factEmbeddings[i], document.Id, fact.Category ?? category);
            memoryRepository.Add(factMemory);
            memoryIds.Add(factMemory.Id);

            foreach (var relation in fact.RelationsToExisting)
            {
                if (!candidatesById.TryGetValue(relation.ExistingMemoryId, out var existingHit))
                {
                    // Ignore relations pointing at memories that weren't in the supplied candidates
                    // (e.g. a hallucinated id) rather than risk a foreign key violation.
                    continue;
                }

                memoryEdgeRepository.Add(new MemoryEdge(spaceId, factMemory.Id, existingHit.Memory.Id, relation.RelationType));

                // Require the same similarity confidence as an explicit forget before an LLM-classified
                // "Updates" relation is allowed to deactivate a memory, so a hallucinated/misclassified
                // relation to a loosely-related candidate can't silently erase it.
                if (relation.RelationType == RelationType.Updates && existingHit.Score >= ForgetSimilarityThreshold)
                {
                    existingHit.Memory.Forget(supersededBy: factMemory.Id);
                    forgottenCount++;
                }
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        var message = forgottenCount > 0
            ? $"Saved {facts.Count} extracted memory(ies) ({forgottenCount} superseded existing memory(ies))."
            : $"Saved {facts.Count} extracted memory(ies).";

        return new AddMemoryResult(memoryIds[0], MemoryAction.Save, facts.Count, message, memoryIds);
    }

    // Batches embedding calls for extracted facts instead of one round trip per fact, and reuses the
    // already-computed content embedding for the common case where a fact's text is the content verbatim.
    private async Task<IReadOnlyList<float[]>> EmbedFactTextsAsync(
        IReadOnlyList<ExtractedFact> facts, string content, float[] contentEmbedding, CancellationToken cancellationToken)
    {
        var toEmbed = facts.Where(f => f.Text != content).Select(f => f.Text).ToList();
        IReadOnlyList<float[]> embedded = toEmbed.Count > 0
            ? await embeddingProvider.EmbedBatchAsync(toEmbed, cancellationToken)
            : [];

        var queue = new Queue<float[]>(embedded);
        return facts.Select(f => f.Text == content ? contentEmbedding : queue.Dequeue()).ToList();
    }

    private async Task<AddMemoryResult> ForgetAsync(Guid spaceId, string content, CancellationToken cancellationToken)
    {
        var embedding = await embeddingProvider.EmbedAsync(content, cancellationToken);
        var candidates = await memoryRepository.SearchAsync(spaceId, embedding, ForgetCandidateTopK, category: null, cancellationToken);

        var toForget = candidates.Where(c => c.Score >= ForgetSimilarityThreshold).ToList();
        foreach (var candidate in toForget)
        {
            candidate.Memory.Forget();
        }

        if (toForget.Count > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var message = toForget.Count > 0
            ? $"Forgot {toForget.Count} matching memory(ies)."
            : "No sufficiently similar memory was found to forget.";

        return new AddMemoryResult(null, MemoryAction.Forget, toForget.Count, message);
    }

    private SpaceGrant RequireAccess(string? containerTag, AccessLevel required)
    {
        var grant = accessContext.ResolveGrant(containerTag) ?? throw new SpaceNotFoundException(containerTag);
        if (!accessContext.HasAccess(grant, required))
        {
            throw new AccessDeniedException(
                $"The current API key does not have {required} access to space '{grant.SpaceKey}'.");
        }

        return grant;
    }

    private static MemorySummaryDto ToSummary(Memory memory) =>
        new(memory.Id, memory.Text, memory.Version, memory.DocumentId, memory.IsActive, memory.CreatedAt, memory.Category);
}
