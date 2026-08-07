using AwesomeAssertions;
using MemoryMcp.Application.Abstractions;
using MemoryMcp.Application.Documents;
using MemoryMcp.Application.Tests.TestSupport;
using MemoryMcp.Domain;
using NSubstitute;

namespace MemoryMcp.Application.Tests.Documents;

public sealed class DocumentServiceTests
{
    private static readonly Guid SpaceId = Guid.NewGuid();
    private static readonly Guid OtherSpaceId = Guid.NewGuid();
    private static readonly SpaceGrant ReadGrant = new(SpaceId, "default", "Default", AccessLevel.Read, IsDefault: true);

    private readonly IDocumentRepository _documentRepository = Substitute.For<IDocumentRepository>();

    private DocumentService CreateService(ICurrentAccessContext accessContext) =>
        new(_documentRepository, accessContext);

    [Fact]
    public async Task ListDocumentsAsync_throws_when_space_cannot_be_resolved()
    {
        var service = CreateService(new FakeAccessContext { Grants = [] });

        var act = async () => await service.ListDocumentsAsync(containerTag: "unknown", page: 1, limit: 10);

        await act.Should().ThrowAsync<SpaceNotFoundException>();
    }

    [Fact]
    public async Task ListDocumentsAsync_returns_clamped_paged_results()
    {
        var document = new Document(SpaceId, "Title", "note", "content");
        _documentRepository.ListAsync(SpaceId, 1, 50, Arg.Any<CancellationToken>())
            .Returns((new[] { document }, 1));

        var service = CreateService(new FakeAccessContext { Grants = [ReadGrant] });

        var result = await service.ListDocumentsAsync(containerTag: null, page: -5, limit: 500);

        result.Page.Should().Be(1);
        result.Limit.Should().Be(50);
        result.Items.Should().ContainSingle(d => d.Id == document.Id && d.Title == "Title");
    }

    [Fact]
    public async Task GetDocumentAsync_throws_when_document_missing()
    {
        _documentRepository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Document?)null);
        var service = CreateService(new FakeAccessContext { Grants = [ReadGrant] });

        var act = async () => await service.GetDocumentAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<EntityNotFoundException>();
    }

    [Fact]
    public async Task GetDocumentAsync_throws_when_key_has_no_grant_for_documents_space()
    {
        var document = new Document(OtherSpaceId, "Title", "note", "content");
        _documentRepository.GetByIdAsync(document.Id, Arg.Any<CancellationToken>()).Returns(document);

        var service = CreateService(new FakeAccessContext { Grants = [ReadGrant] });

        var act = async () => await service.GetDocumentAsync(document.Id);

        await act.Should().ThrowAsync<AccessDeniedException>();
    }

    [Fact]
    public async Task GetDocumentAsync_returns_detail_when_access_granted()
    {
        var document = new Document(SpaceId, "Title", "note", "raw content", "summary");
        _documentRepository.GetByIdAsync(document.Id, Arg.Any<CancellationToken>()).Returns(document);

        var service = CreateService(new FakeAccessContext { Grants = [ReadGrant] });

        var result = await service.GetDocumentAsync(document.Id);

        result.RawContent.Should().Be("raw content");
        result.Summary.Should().Be("summary");
    }
}
