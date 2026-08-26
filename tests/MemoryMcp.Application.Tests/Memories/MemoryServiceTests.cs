using AwesomeAssertions;
using MemoryMcp.Application.Abstractions;
using MemoryMcp.Application.Memories;
using MemoryMcp.Application.Tests.TestSupport;
using MemoryMcp.Domain;
using NSubstitute;
using Memory = MemoryMcp.Domain.Memory;

namespace MemoryMcp.Application.Tests.Memories;

public sealed class MemoryServiceTests
{
    private const int ExtractionCandidateTopK = 5;

    private static readonly Guid SpaceId = Guid.NewGuid();
    private static readonly SpaceGrant ReadWriteGrant = new(SpaceId, "default", "Default", AccessLevel.ReadWrite, IsDefault: true);
    private static readonly SpaceGrant ReadOnlyGrant = new(SpaceId, "default", "Default", AccessLevel.Read, IsDefault: true);

    private readonly IMemoryRepository _memoryRepository = Substitute.For<IMemoryRepository>();
    private readonly IDocumentRepository _documentRepository = Substitute.For<IDocumentRepository>();
    private readonly IEmbeddingProvider _embeddingProvider = Substitute.For<IEmbeddingProvider>();
    private readonly IMemoryEdgeRepository _memoryEdgeRepository = Substitute.For<IMemoryEdgeRepository>();
    private readonly IFactExtractor _factExtractor = Substitute.For<IFactExtractor>();
    private readonly IMemoryGraphService _memoryGraphService = Substitute.For<IMemoryGraphService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    public MemoryServiceTests()
    {
        // Default: extraction unconfigured, no related memories — matches today's pre-graph-memory
        // behavior unless a test overrides these stubs.
        _factExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MemoryCandidateDto>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExtractedFact>());
        _memoryGraphService.GetRelatedAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RelatedMemoryDto>());
    }

    private MemoryService CreateService(ICurrentAccessContext accessContext) =>
        new(_memoryRepository, _documentRepository, _embeddingProvider, _memoryEdgeRepository, _factExtractor, _memoryGraphService, _unitOfWork, accessContext);

    [Fact]
    public async Task SearchMemoryAsync_throws_when_space_cannot_be_resolved()
    {
        var service = CreateService(new FakeAccessContext { Grants = [] });

        var act = async () => await service.SearchMemoryAsync("hello", keyword: null, category: null, includeProfile: false, containerTag: null);

        await act.Should().ThrowAsync<SpaceNotFoundException>();
    }

    [Fact]
    public async Task SearchMemoryAsync_returns_matches_and_profile_when_requested()
    {
        var embedding = new float[] { 0.1f, 0.2f };
        _embeddingProvider.EmbedAsync("hello", Arg.Any<CancellationToken>()).Returns(embedding);

        var hit = new MemorySearchHit(new Memory(SpaceId, "hit text", embedding), 0.9);
        _memoryRepository.SearchAsync(SpaceId, embedding, 10, category: null, Arg.Any<CancellationToken>())
            .Returns(new[] { hit });

        var recent = new Memory(SpaceId, "recent text", embedding);
        _memoryRepository.ListRecentActiveAsync(SpaceId, 5, Arg.Any<CancellationToken>())
            .Returns(new[] { recent });

        var service = CreateService(new FakeAccessContext { Grants = [ReadOnlyGrant] });

        var result = await service.SearchMemoryAsync("hello", keyword: null, category: null, includeProfile: true, containerTag: null);

        result.Matches.Should().ContainSingle(m => m.Text == "hit text" && m.Score == 0.9);
        result.Profile.Should().ContainSingle(m => m.Text == "recent text");
    }

    [Fact]
    public async Task SearchMemoryAsync_attaches_related_memories_to_top_matches()
    {
        var embedding = new float[] { 0.1f, 0.2f };
        _embeddingProvider.EmbedAsync("hello", Arg.Any<CancellationToken>()).Returns(embedding);

        var matchMemory = new Memory(SpaceId, "hit text", embedding);
        var hit = new MemorySearchHit(matchMemory, 0.9);
        _memoryRepository.SearchAsync(SpaceId, embedding, 10, category: null, Arg.Any<CancellationToken>())
            .Returns(new[] { hit });

        var relatedId = Guid.NewGuid();
        _memoryGraphService.GetRelatedAsync(matchMemory.Id, SpaceId, 2, Arg.Any<CancellationToken>())
            .Returns(new[] { new RelatedMemoryDto(relatedId, "related text", RelationType.Extends, 1) });

        var service = CreateService(new FakeAccessContext { Grants = [ReadOnlyGrant] });

        var result = await service.SearchMemoryAsync("hello", keyword: null, category: null, includeProfile: false, containerTag: null);

        result.Matches.Should().ContainSingle(m => m.RelatedMemories != null && m.RelatedMemories.Any(r => r.Id == relatedId));
    }

    [Fact]
    public async Task SearchMemoryAsync_keyword_search_matches_literal_text_without_embedding()
    {
        var match = new Memory(SpaceId, "the sky is blue", embedding: null);
        _memoryRepository.SearchByKeywordAsync(SpaceId, "sky", 10, category: null, Arg.Any<CancellationToken>())
            .Returns(new[] { match });

        var service = CreateService(new FakeAccessContext { Grants = [ReadOnlyGrant] });

        var result = await service.SearchMemoryAsync(query: null, keyword: "sky", category: null, includeProfile: false, containerTag: null);

        result.Matches.Should().ContainSingle(m => m.Text == "the sky is blue");
        await _embeddingProvider.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchMemoryAsync_category_only_lists_memories_in_that_category()
    {
        var match = new Memory(SpaceId, "tagged memory", embedding: null, category: "work");
        _memoryRepository.ListByCategoryAsync(SpaceId, "work", 10, Arg.Any<CancellationToken>())
            .Returns(new[] { match });

        var service = CreateService(new FakeAccessContext { Grants = [ReadOnlyGrant] });

        var result = await service.SearchMemoryAsync(query: null, keyword: null, category: "work", includeProfile: false, containerTag: null);

        result.Matches.Should().ContainSingle(m => m.Text == "tagged memory" && m.Category == "work");
    }

    [Fact]
    public async Task SearchMemoryAsync_throws_when_no_query_keyword_or_category_is_given()
    {
        var service = CreateService(new FakeAccessContext { Grants = [ReadOnlyGrant] });

        var act = async () => await service.SearchMemoryAsync(query: null, keyword: null, category: null, includeProfile: false, containerTag: null);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task AddMemoryAsync_rejects_empty_content()
    {
        var service = CreateService(new FakeAccessContext { Grants = [ReadWriteGrant] });

        var act = async () => await service.AddMemoryAsync("   ", MemoryAction.Save, category: null, containerTag: null);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task AddMemoryAsync_save_requires_read_write_access()
    {
        var service = CreateService(new FakeAccessContext { Grants = [ReadOnlyGrant] });

        var act = async () => await service.AddMemoryAsync("content", MemoryAction.Save, category: null, containerTag: null);

        await act.Should().ThrowAsync<AccessDeniedException>();
    }

    [Fact]
    public async Task AddMemoryAsync_save_falls_back_to_a_single_memory_when_extraction_yields_no_facts()
    {
        var embedding = new float[] { 0.3f };
        _embeddingProvider.EmbedAsync("remember this", Arg.Any<CancellationToken>()).Returns(embedding);
        _memoryRepository.SearchAsync(SpaceId, embedding, ExtractionCandidateTopK, category: null, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MemorySearchHit>());

        var service = CreateService(new FakeAccessContext { Grants = [ReadWriteGrant] });

        var result = await service.AddMemoryAsync("remember this", MemoryAction.Save, category: null, containerTag: null);

        result.Action.Should().Be(MemoryAction.Save);
        result.MemoryId.Should().NotBeNull();
        result.AffectedCount.Should().Be(1);

        _documentRepository.Received(1).Add(Arg.Is<Document>(d => d != null && d.SpaceId == SpaceId && d.RawContent == "remember this"));
        _memoryRepository.Received(1).Add(Arg.Is<Memory>(m => m != null && m.SpaceId == SpaceId && m.Text == "remember this"));
        _memoryEdgeRepository.DidNotReceive().Add(Arg.Any<MemoryEdge>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMemoryAsync_save_falls_back_to_a_single_memory_when_extractor_is_not_configured()
    {
        var embedding = new float[] { 0.3f };
        _embeddingProvider.EmbedAsync("remember this", Arg.Any<CancellationToken>()).Returns(embedding);
        _memoryRepository.SearchAsync(SpaceId, embedding, ExtractionCandidateTopK, category: null, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MemorySearchHit>());
        _factExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MemoryCandidateDto>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<ExtractedFact>>(new ExtractorNotConfiguredException("not configured")));

        var service = CreateService(new FakeAccessContext { Grants = [ReadWriteGrant] });

        var result = await service.AddMemoryAsync("remember this", MemoryAction.Save, category: null, containerTag: null);

        result.AffectedCount.Should().Be(1);
        _memoryRepository.Received(1).Add(Arg.Is<Memory>(m => m != null && m.Text == "remember this"));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMemoryAsync_save_creates_a_memory_and_edge_per_extracted_fact_and_forgets_updated_memory()
    {
        var contentEmbedding = new float[] { 0.3f };
        _embeddingProvider.EmbedAsync("Alex left Stripe and joined a startup", Arg.Any<CancellationToken>()).Returns(contentEmbedding);

        var existing = new Memory(SpaceId, "Alex is a PM at Stripe", contentEmbedding);
        var candidateHit = new MemorySearchHit(existing, 0.85);
        _memoryRepository.SearchAsync(SpaceId, contentEmbedding, ExtractionCandidateTopK, category: null, Arg.Any<CancellationToken>())
            .Returns(new[] { candidateHit });

        var factEmbedding = new float[] { 0.4f };
        _embeddingProvider.EmbedBatchAsync(Arg.Is<IReadOnlyList<string>>(l => l != null && l.SequenceEqual(new[] { "Alex left Stripe" })), Arg.Any<CancellationToken>())
            .Returns(new[] { factEmbedding });

        var extractedFact = new ExtractedFact(
            "Alex left Stripe", Category: null, RelationsToExisting: [new ExtractedRelation(existing.Id, RelationType.Updates)]);
        _factExtractor.ExtractAsync("Alex left Stripe and joined a startup", Arg.Any<IReadOnlyList<MemoryCandidateDto>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { extractedFact });

        var service = CreateService(new FakeAccessContext { Grants = [ReadWriteGrant] });

        var result = await service.AddMemoryAsync("Alex left Stripe and joined a startup", MemoryAction.Save, category: null, containerTag: null);

        result.AffectedCount.Should().Be(1);
        result.MemoryIds.Should().HaveCount(1);
        _memoryRepository.Received(1).Add(Arg.Is<Memory>(m => m != null && m.Text == "Alex left Stripe"));
        _memoryEdgeRepository.Received(1).Add(Arg.Is<MemoryEdge>(e =>
            e != null && e.SpaceId == SpaceId && e.ToMemoryId == existing.Id && e.RelationType == RelationType.Updates));
        existing.IsActive.Should().BeFalse();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMemoryAsync_save_does_not_forget_an_updates_target_below_the_similarity_threshold()
    {
        var contentEmbedding = new float[] { 0.3f };
        _embeddingProvider.EmbedAsync("Alex left Stripe and joined a startup", Arg.Any<CancellationToken>()).Returns(contentEmbedding);

        // Below ForgetSimilarityThreshold (0.8): a loosely-related/hallucinated "Updates" classification
        // should still create the edge (the relation itself may be informative), but must not deactivate it.
        var existing = new Memory(SpaceId, "Alex is a PM at Stripe", contentEmbedding);
        var candidateHit = new MemorySearchHit(existing, 0.5);
        _memoryRepository.SearchAsync(SpaceId, contentEmbedding, ExtractionCandidateTopK, category: null, Arg.Any<CancellationToken>())
            .Returns(new[] { candidateHit });

        var factEmbedding = new float[] { 0.4f };
        _embeddingProvider.EmbedBatchAsync(Arg.Is<IReadOnlyList<string>>(l => l != null && l.SequenceEqual(new[] { "Alex left Stripe" })), Arg.Any<CancellationToken>())
            .Returns(new[] { factEmbedding });

        var extractedFact = new ExtractedFact(
            "Alex left Stripe", Category: null, RelationsToExisting: [new ExtractedRelation(existing.Id, RelationType.Updates)]);
        _factExtractor.ExtractAsync("Alex left Stripe and joined a startup", Arg.Any<IReadOnlyList<MemoryCandidateDto>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { extractedFact });

        var service = CreateService(new FakeAccessContext { Grants = [ReadWriteGrant] });

        await service.AddMemoryAsync("Alex left Stripe and joined a startup", MemoryAction.Save, category: null, containerTag: null);

        _memoryEdgeRepository.Received(1).Add(Arg.Any<MemoryEdge>());
        existing.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task AddMemoryAsync_save_scopes_the_candidate_search_to_the_callers_category()
    {
        var contentEmbedding = new float[] { 0.3f };
        _embeddingProvider.EmbedAsync("invoice due the 5th", Arg.Any<CancellationToken>()).Returns(contentEmbedding);
        _memoryRepository.SearchAsync(SpaceId, contentEmbedding, ExtractionCandidateTopK, "finance", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MemorySearchHit>());

        var service = CreateService(new FakeAccessContext { Grants = [ReadWriteGrant] });

        await service.AddMemoryAsync("invoice due the 5th", MemoryAction.Save, category: "finance", containerTag: null);

        await _memoryRepository.Received(1).SearchAsync(SpaceId, contentEmbedding, ExtractionCandidateTopK, "finance", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMemoryAsync_save_reuses_the_content_embedding_for_a_fact_matching_content_verbatim()
    {
        var contentEmbedding = new float[] { 0.3f };
        _embeddingProvider.EmbedAsync("The sky is blue", Arg.Any<CancellationToken>()).Returns(contentEmbedding);
        _memoryRepository.SearchAsync(SpaceId, contentEmbedding, ExtractionCandidateTopK, category: null, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MemorySearchHit>());

        var extractedFact = new ExtractedFact("The sky is blue", Category: null, RelationsToExisting: []);
        _factExtractor.ExtractAsync("The sky is blue", Arg.Any<IReadOnlyList<MemoryCandidateDto>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { extractedFact });

        var service = CreateService(new FakeAccessContext { Grants = [ReadWriteGrant] });

        await service.AddMemoryAsync("The sky is blue", MemoryAction.Save, category: null, containerTag: null);

        _memoryRepository.Received(1).Add(Arg.Is<Memory>(m => m != null && m.Embedding == contentEmbedding));
        await _embeddingProvider.DidNotReceive().EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMemoryAsync_save_ignores_relations_to_ids_outside_the_supplied_candidates()
    {
        var contentEmbedding = new float[] { 0.3f };
        _embeddingProvider.EmbedAsync("some content", Arg.Any<CancellationToken>()).Returns(contentEmbedding);
        _memoryRepository.SearchAsync(SpaceId, contentEmbedding, ExtractionCandidateTopK, category: null, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MemorySearchHit>());

        var factEmbedding = new float[] { 0.4f };
        _embeddingProvider.EmbedBatchAsync(Arg.Is<IReadOnlyList<string>>(l => l != null && l.SequenceEqual(new[] { "a fact" })), Arg.Any<CancellationToken>())
            .Returns(new[] { factEmbedding });

        var hallucinatedId = Guid.NewGuid();
        var extractedFact = new ExtractedFact("a fact", Category: null, RelationsToExisting: [new ExtractedRelation(hallucinatedId, RelationType.Extends)]);
        _factExtractor.ExtractAsync("some content", Arg.Any<IReadOnlyList<MemoryCandidateDto>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { extractedFact });

        var service = CreateService(new FakeAccessContext { Grants = [ReadWriteGrant] });

        await service.AddMemoryAsync("some content", MemoryAction.Save, category: null, containerTag: null);

        _memoryEdgeRepository.DidNotReceive().Add(Arg.Any<MemoryEdge>());
    }

    [Fact]
    public async Task AddMemoryAsync_forget_marks_only_sufficiently_similar_memories_inactive()
    {
        var embedding = new float[] { 0.5f };
        _embeddingProvider.EmbedAsync("obsolete fact", Arg.Any<CancellationToken>()).Returns(embedding);

        var strongMatch = new Memory(SpaceId, "obsolete fact", embedding);
        var weakMatch = new Memory(SpaceId, "unrelated fact", embedding);

        _memoryRepository.SearchAsync(SpaceId, embedding, 3, category: null, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new MemorySearchHit(strongMatch, 0.95),
                new MemorySearchHit(weakMatch, 0.4),
            });

        var service = CreateService(new FakeAccessContext { Grants = [ReadWriteGrant] });

        var result = await service.AddMemoryAsync("obsolete fact", MemoryAction.Forget, category: null, containerTag: null);

        result.AffectedCount.Should().Be(1);
        strongMatch.IsActive.Should().BeFalse();
        weakMatch.IsActive.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMemoryAsync_forget_does_not_save_when_nothing_matches()
    {
        var embedding = new float[] { 0.5f };
        _embeddingProvider.EmbedAsync("nothing like this", Arg.Any<CancellationToken>()).Returns(embedding);
        _memoryRepository.SearchAsync(SpaceId, embedding, 3, category: null, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MemorySearchHit>());

        var service = CreateService(new FakeAccessContext { Grants = [ReadWriteGrant] });

        var result = await service.AddMemoryAsync("nothing like this", MemoryAction.Forget, category: null, containerTag: null);

        result.AffectedCount.Should().Be(0);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListMemoriesAsync_clamps_paging_and_maps_results()
    {
        var memory = new Memory(SpaceId, "text", embedding: null);
        _memoryRepository.ListAsync(SpaceId, 1, 10, Arg.Any<CancellationToken>())
            .Returns((new[] { memory }, 1));

        var service = CreateService(new FakeAccessContext { Grants = [ReadOnlyGrant] });

        var result = await service.ListMemoriesAsync(containerTag: null, page: 0, limit: 0);

        result.Page.Should().Be(1);
        result.Limit.Should().Be(10);
        result.Items.Should().ContainSingle(m => m.Id == memory.Id);
    }
}
