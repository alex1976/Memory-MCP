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
    private static readonly Guid SpaceId = Guid.NewGuid();
    private static readonly SpaceGrant ReadWriteGrant = new(SpaceId, "default", "Default", AccessLevel.ReadWrite, IsDefault: true);
    private static readonly SpaceGrant ReadOnlyGrant = new(SpaceId, "default", "Default", AccessLevel.Read, IsDefault: true);

    private readonly IMemoryRepository _memoryRepository = Substitute.For<IMemoryRepository>();
    private readonly IDocumentRepository _documentRepository = Substitute.For<IDocumentRepository>();
    private readonly IEmbeddingProvider _embeddingProvider = Substitute.For<IEmbeddingProvider>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private MemoryService CreateService(ICurrentAccessContext accessContext) =>
        new(_memoryRepository, _documentRepository, _embeddingProvider, _unitOfWork, accessContext);

    [Fact]
    public async Task SearchMemoryAsync_throws_when_space_cannot_be_resolved()
    {
        var service = CreateService(new FakeAccessContext { Grants = [] });

        var act = async () => await service.SearchMemoryAsync("hello", includeProfile: false, containerTag: null);

        await act.Should().ThrowAsync<SpaceNotFoundException>();
    }

    [Fact]
    public async Task SearchMemoryAsync_returns_matches_and_profile_when_requested()
    {
        var embedding = new float[] { 0.1f, 0.2f };
        _embeddingProvider.EmbedAsync("hello", Arg.Any<CancellationToken>()).Returns(embedding);

        var hit = new MemorySearchHit(new Memory(SpaceId, "hit text", embedding), 0.9);
        _memoryRepository.SearchAsync(SpaceId, embedding, 10, Arg.Any<CancellationToken>())
            .Returns(new[] { hit });

        var recent = new Memory(SpaceId, "recent text", embedding);
        _memoryRepository.ListRecentActiveAsync(SpaceId, 5, Arg.Any<CancellationToken>())
            .Returns(new[] { recent });

        var service = CreateService(new FakeAccessContext { Grants = [ReadOnlyGrant] });

        var result = await service.SearchMemoryAsync("hello", includeProfile: true, containerTag: null);

        result.Matches.Should().ContainSingle(m => m.Text == "hit text" && m.Score == 0.9);
        result.Profile.Should().ContainSingle(m => m.Text == "recent text");
    }

    [Fact]
    public async Task AddMemoryAsync_save_requires_read_write_access()
    {
        var service = CreateService(new FakeAccessContext { Grants = [ReadOnlyGrant] });

        var act = async () => await service.AddMemoryAsync("content", MemoryAction.Save, containerTag: null);

        await act.Should().ThrowAsync<AccessDeniedException>();
    }

    [Fact]
    public async Task AddMemoryAsync_save_creates_document_and_memory_and_persists()
    {
        var embedding = new float[] { 0.3f };
        _embeddingProvider.EmbedAsync("remember this", Arg.Any<CancellationToken>()).Returns(embedding);

        var service = CreateService(new FakeAccessContext { Grants = [ReadWriteGrant] });

        var result = await service.AddMemoryAsync("remember this", MemoryAction.Save, containerTag: null);

        result.Action.Should().Be(MemoryAction.Save);
        result.MemoryId.Should().NotBeNull();
        result.AffectedCount.Should().Be(1);

        _documentRepository.Received(1).Add(Arg.Is<Document>(d => d != null && d.SpaceId == SpaceId && d.RawContent == "remember this"));
        _memoryRepository.Received(1).Add(Arg.Is<Memory>(m => m != null && m.SpaceId == SpaceId && m.Text == "remember this"));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMemoryAsync_forget_marks_only_sufficiently_similar_memories_inactive()
    {
        var embedding = new float[] { 0.5f };
        _embeddingProvider.EmbedAsync("obsolete fact", Arg.Any<CancellationToken>()).Returns(embedding);

        var strongMatch = new Memory(SpaceId, "obsolete fact", embedding);
        var weakMatch = new Memory(SpaceId, "unrelated fact", embedding);

        _memoryRepository.SearchAsync(SpaceId, embedding, 3, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new MemorySearchHit(strongMatch, 0.95),
                new MemorySearchHit(weakMatch, 0.4),
            });

        var service = CreateService(new FakeAccessContext { Grants = [ReadWriteGrant] });

        var result = await service.AddMemoryAsync("obsolete fact", MemoryAction.Forget, containerTag: null);

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
        _memoryRepository.SearchAsync(SpaceId, embedding, 3, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MemorySearchHit>());

        var service = CreateService(new FakeAccessContext { Grants = [ReadWriteGrant] });

        var result = await service.AddMemoryAsync("nothing like this", MemoryAction.Forget, containerTag: null);

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
