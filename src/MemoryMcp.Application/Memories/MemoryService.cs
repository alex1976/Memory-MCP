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
    IUserRepository userRepository,
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
        // A search is always confined to exactly one space, whichever the tag resolves to: there is no
        // cross-space read path, so a caller holding grants on several spaces still has to name the one
        // they mean. Within that space nothing is filtered by author — every member reads the whole
        // space's knowledge, and authorship is reported rather than used to restrict.
        var grant = RequireAccess(containerTag, AccessLevel.Read);

        List<(Memory Memory, double Score)> hits;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var embedding = await embeddingProvider.EmbedAsync(query, cancellationToken);
            var semanticHits = await memoryRepository.SearchAsync(grant.SpaceId, embedding, SearchTopK, category, cancellationToken);
            hits = semanticHits.Select(h => (h.Memory, h.Score)).ToList();
        }
        else if (!string.IsNullOrWhiteSpace(keyword))
        {
            var keywordHits = await memoryRepository.SearchByKeywordAsync(grant.SpaceId, keyword, SearchTopK, category, cancellationToken);
            hits = keywordHits.Select(m => (m, 1.0)).ToList();
        }
        else if (!string.IsNullOrWhiteSpace(category))
        {
            var categoryHits = await memoryRepository.ListByCategoryAsync(grant.SpaceId, category, SearchTopK, cancellationToken);
            hits = categoryHits.Select(m => (m, 1.0)).ToList();
        }
        else
        {
            throw new ValidationException("Provide at least one of query, keyword, or category.");
        }

        var attribution = await UserAttribution.LoadAsync(
            userRepository, hits.Select(h => h.Memory.CreatedByUserId), cancellationToken);

        var matches = hits
            .Select(h => new MemorySearchResultDto(
                h.Memory.Id, h.Memory.Text, h.Score, h.Memory.DocumentId, h.Memory.Category,
                CreatedByUserId: h.Memory.CreatedByUserId,
                CreatedBy: attribution.DisplayName(h.Memory.CreatedByUserId)))
            .ToList();

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
        return await ToSummariesAsync(recent, cancellationToken);
    }

    public async Task<AddMemoryResult> AddMemoryAsync(
        string content, MemoryAction action, string? category, string? containerTag, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            // Without this, a save would persist an empty memory (and embed an empty string), and a
            // forget would match arbitrary memories by whatever the empty-text embedding happens to be.
            throw new ValidationException("Content must not be empty.");
        }

        // ReadWrite here is the *effective* level: a Reader's grant was capped to Read when the access
        // snapshot was built, so a read-only member is refused regardless of what their grant row says.
        var grant = RequireAccess(containerTag, AccessLevel.ReadWrite);
        var userId = accessContext.User.Id;

        return action switch
        {
            MemoryAction.Save => await SaveAsync(grant.SpaceId, userId, content, category, cancellationToken),
            MemoryAction.Forget => await ForgetAsync(grant.SpaceId, userId, content, cancellationToken),
            _ => throw new ValidationException($"Unsupported memory action '{action}'. Use 'save' or 'forget'."),
        };
    }

    public async Task<PagedResult<MemorySummaryDto>> ListMemoriesAsync(
        string? containerTag, int page, int limit, CancellationToken cancellationToken = default)
    {
        var grant = RequireAccess(containerTag, AccessLevel.Read);

        var (clampedPage, clampedLimit) = Paging.Clamp(page, limit);
        var (items, totalCount) = await memoryRepository.ListAsync(grant.SpaceId, clampedPage, clampedLimit, cancellationToken);

        var dtos = await ToSummariesAsync(items, cancellationToken);
        return new PagedResult<MemorySummaryDto>(dtos, clampedPage, clampedLimit, totalCount);
    }

    private async Task<AddMemoryResult> SaveAsync(
        Guid spaceId, Guid userId, string content, string? category, CancellationToken cancellationToken)
    {
        var title = content.Length > 80 ? content[..80] + "…" : content;
        var document = new Document(spaceId, title, docType: "note", rawContent: content, createdByUserId: userId);
        document.MarkProcessed(summary: null, byUserId: userId);
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
            var memory = new Memory(spaceId, content, contentEmbedding, document.Id, category, createdByUserId: userId);
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
            var factMemory = new Memory(
                spaceId, fact.Text, factEmbeddings[i], document.Id, fact.Category ?? category, createdByUserId: userId);
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

                memoryEdgeRepository.Add(new MemoryEdge(
                    spaceId, factMemory.Id, existingHit.Memory.Id, relation.RelationType, relation.Note));

                // Require the same similarity confidence as an explicit forget before an LLM-classified
                // "Updates" relation is allowed to deactivate a memory, so a hallucinated/misclassified
                // relation to a loosely-related candidate can't silently erase it.
                if (relation.RelationType == RelationType.Updates && existingHit.Score >= ForgetSimilarityThreshold)
                {
                    // The superseded memory may well be a colleague's, so stamp the member who caused the
                    // deactivation onto it — otherwise a shared space loses all record of who erased what.
                    existingHit.Memory.Forget(byUserId: userId, supersededBy: factMemory.Id);
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

        if (embedded.Count != toEmbed.Count)
        {
            // Positional zip: a short/over-long batch would pair every subsequent fact with another
            // fact's vector and silently poison similarity search. Previously this surfaced as an
            // opaque "queue empty" InvalidOperationException, so name the actual problem instead.
            throw new InvalidOperationException(
                $"Embedding provider returned {embedded.Count} vectors for {toEmbed.Count} texts.");
        }

        var nextEmbedding = 0;
        return facts.Select(f => f.Text == content ? contentEmbedding : embedded[nextEmbedding++]).ToList();
    }

    private async Task<AddMemoryResult> ForgetAsync(
        Guid spaceId, Guid userId, string content, CancellationToken cancellationToken)
    {
        var embedding = await embeddingProvider.EmbedAsync(content, cancellationToken);
        var candidates = await memoryRepository.SearchAsync(spaceId, embedding, ForgetCandidateTopK, category: null, cancellationToken);

        var toForget = candidates.Where(c => c.Score >= ForgetSimilarityThreshold).ToList();
        foreach (var candidate in toForget)
        {
            candidate.Memory.Forget(byUserId: userId);
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

    public async Task<SpaceGraphDto> GetSpaceGraphAsync(string? containerTag, CancellationToken cancellationToken = default)
    {
        var grant = RequireAccess(containerTag, AccessLevel.Read);
        return await memoryGraphService.GetSpaceGraphAsync(grant.SpaceId, cancellationToken: cancellationToken);
    }

    private SpaceGrant RequireAccess(string? containerTag, AccessLevel required) =>
        accessContext.RequireSpace(containerTag, required);

    private async Task<List<MemorySummaryDto>> ToSummariesAsync(
        IReadOnlyList<Memory> memories, CancellationToken cancellationToken)
    {
        var attribution = await UserAttribution.LoadAsync(
            userRepository,
            memories.SelectMany(m => new[] { m.CreatedByUserId, m.UpdatedByUserId }),
            cancellationToken);

        return memories.Select(m => ToSummary(m, attribution)).ToList();
    }

    private static MemorySummaryDto ToSummary(Memory memory, UserAttribution attribution) =>
        new(memory.Id, memory.Text, memory.Version, memory.DocumentId, memory.IsActive, memory.CreatedAt, memory.Category,
            CreatedByUserId: memory.CreatedByUserId,
            CreatedBy: attribution.DisplayName(memory.CreatedByUserId),
            UpdatedByUserId: memory.UpdatedByUserId,
            UpdatedBy: attribution.DisplayName(memory.UpdatedByUserId));
}
