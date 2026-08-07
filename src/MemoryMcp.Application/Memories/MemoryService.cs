using MemoryMcp.Application.Abstractions;
using MemoryMcp.Domain;

namespace MemoryMcp.Application.Memories;

public sealed class MemoryService(
    IMemoryRepository memoryRepository,
    IDocumentRepository documentRepository,
    IEmbeddingProvider embeddingProvider,
    IUnitOfWork unitOfWork,
    ICurrentAccessContext accessContext) : IMemoryService
{
    private const int SearchTopK = 10;
    private const int ProfileTake = 5;
    private const int ForgetCandidateTopK = 3;
    private const double ForgetSimilarityThreshold = 0.8;

    public async Task<SearchMemoryResult> SearchMemoryAsync(
        string query, bool includeProfile, string? containerTag, CancellationToken cancellationToken = default)
    {
        var grant = RequireAccess(containerTag, AccessLevel.Read);

        var embedding = await embeddingProvider.EmbedAsync(query, cancellationToken);
        var hits = await memoryRepository.SearchAsync(grant.SpaceId, embedding, SearchTopK, cancellationToken);
        var matches = hits.Select(h => new MemorySearchResultDto(h.Memory.Id, h.Memory.Text, h.Score, h.Memory.DocumentId)).ToList();

        IReadOnlyList<MemorySummaryDto>? profile = null;
        if (includeProfile)
        {
            var recent = await memoryRepository.ListRecentActiveAsync(grant.SpaceId, ProfileTake, cancellationToken);
            profile = recent.Select(ToSummary).ToList();
        }

        return new SearchMemoryResult(matches, profile);
    }

    public async Task<AddMemoryResult> AddMemoryAsync(
        string content, MemoryAction action, string? containerTag, CancellationToken cancellationToken = default)
    {
        var grant = RequireAccess(containerTag, AccessLevel.ReadWrite);

        return action switch
        {
            MemoryAction.Save => await SaveAsync(grant.SpaceId, content, cancellationToken),
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

    private async Task<AddMemoryResult> SaveAsync(Guid spaceId, string content, CancellationToken cancellationToken)
    {
        var title = content.Length > 80 ? content[..80] + "…" : content;
        var document = new Document(spaceId, title, docType: "note", rawContent: content);
        document.MarkProcessed(summary: null);
        documentRepository.Add(document);

        var embedding = await embeddingProvider.EmbedAsync(content, cancellationToken);
        var memory = new Memory(spaceId, content, embedding, document.Id);
        memoryRepository.Add(memory);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new AddMemoryResult(memory.Id, MemoryAction.Save, 1, "Memory saved.");
    }

    private async Task<AddMemoryResult> ForgetAsync(Guid spaceId, string content, CancellationToken cancellationToken)
    {
        var embedding = await embeddingProvider.EmbedAsync(content, cancellationToken);
        var candidates = await memoryRepository.SearchAsync(spaceId, embedding, ForgetCandidateTopK, cancellationToken);

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
        new(memory.Id, memory.Text, memory.Version, memory.DocumentId, memory.IsActive, memory.CreatedAt);
}
