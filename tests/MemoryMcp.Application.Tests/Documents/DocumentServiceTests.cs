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

    private static readonly SpaceGrant ReadWriteGrant = new(SpaceId, "default", "Default", AccessLevel.ReadWrite, IsDefault: true);

    private readonly IDocumentRepository _documentRepository = Substitute.For<IDocumentRepository>();
    private readonly IUserRepository _userRepository = Substitute.For<IUserRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IPdfTextExtractor _pdfTextExtractor = Substitute.For<IPdfTextExtractor>();

    public DocumentServiceTests() =>
        _userRepository.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<UserSummary>());

    private DocumentService CreateService(ICurrentAccessContext accessContext) =>
        new(_documentRepository, _userRepository, accessContext, _unitOfWork, _pdfTextExtractor);

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

    [Fact]
    public async Task CreateDocumentAsync_persists_and_returns_a_processed_document()
    {
        var service = CreateService(new FakeAccessContext { Grants = [ReadWriteGrant] });

        var result = await service.CreateDocumentAsync("Notes", "text", "raw content", "a summary", containerTag: null);

        result.Title.Should().Be("Notes");
        result.DocType.Should().Be("text");
        result.Summary.Should().Be("a summary");
        result.Status.Should().Be(DocumentStatus.Processed.ToString());
        _documentRepository.Received(1).Add(Arg.Is<Document>(d => d != null && d.Title == "Notes" && d.SpaceId == SpaceId));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateDocumentAsync_throws_when_key_only_has_read_access()
    {
        var service = CreateService(new FakeAccessContext { Grants = [ReadGrant] });

        var act = async () => await service.CreateDocumentAsync("Notes", "text", "raw content", null, containerTag: null);

        await act.Should().ThrowAsync<AccessDeniedException>();
    }

    [Fact]
    public async Task CreateDocumentAsync_extracts_pdf_text_instead_of_storing_the_base64_payload()
    {
        var pdfBytes = "%PDF-1.4 fake bytes"u8.ToArray();
        var base64 = Convert.ToBase64String(pdfBytes);
        _pdfTextExtractor.ExtractTextAsync(Arg.Is<byte[]>(b => b.SequenceEqual(pdfBytes)), Arg.Any<CancellationToken>())
            .Returns("Extracted PDF text");

        var service = CreateService(new FakeAccessContext { Grants = [ReadWriteGrant] });

        var result = await service.CreateDocumentAsync("report.pdf", "pdf", base64, null, containerTag: null);

        result.DocType.Should().Be("pdf");
        _documentRepository.Received(1).Add(Arg.Is<Document>(d => d != null && d.RawContent == "Extracted PDF text"));
    }

    [Fact]
    public async Task CreateDocumentAsync_throws_when_pdf_content_is_not_valid_base64()
    {
        var service = CreateService(new FakeAccessContext { Grants = [ReadWriteGrant] });

        var act = async () => await service.CreateDocumentAsync("report.pdf", "pdf", "not-base64!!", null, containerTag: null);

        await act.Should().ThrowAsync<DocumentExtractionException>();
    }

    [Fact]
    public async Task CreateDocumentAsync_attributes_the_document_to_the_calling_user()
    {
        var alice = new CurrentUser(Guid.NewGuid(), "alice@team.test", "Alice", UserRole.Writer);
        var service = CreateService(new FakeAccessContext { User = alice, Grants = [ReadWriteGrant] });

        var result = await service.CreateDocumentAsync("Notes", "text", "raw content", null, containerTag: null);

        result.CreatedByUserId.Should().Be(alice.Id);
        result.CreatedBy.Should().Be("Alice");
        _documentRepository.Received(1).Add(Arg.Is<Document>(d =>
            d != null && d.CreatedByUserId == alice.Id && d.UpdatedByUserId == alice.Id));
    }

    [Fact]
    public async Task ListDocumentsAsync_returns_other_members_documents_and_names_their_author()
    {
        var alice = new CurrentUser(Guid.NewGuid(), "alice@team.test", "Alice", UserRole.Writer);
        var bobId = Guid.NewGuid();
        _userRepository.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new UserSummary(bobId, "bob@team.test", "Bob", UserRole.Writer) });

        var bobsDocument = new Document(SpaceId, "Bob's upload", "text", "content", createdByUserId: bobId);
        _documentRepository.ListAsync(SpaceId, 1, 10, Arg.Any<CancellationToken>())
            .Returns((new[] { bobsDocument }, 1));

        var service = CreateService(new FakeAccessContext { User = alice, Grants = [ReadGrant] });

        var result = await service.ListDocumentsAsync(containerTag: null, page: 1, limit: 10);

        result.Items.Should().ContainSingle(d => d.CreatedBy == "Bob" && d.CreatedByUserId == bobId);
    }

    [Fact]
    public async Task GetDocumentAsync_names_the_author_of_another_members_document()
    {
        var bobId = Guid.NewGuid();
        _userRepository.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new UserSummary(bobId, "bob@team.test", "Bob", UserRole.Writer) });

        var document = new Document(SpaceId, "Title", "note", "raw content", "summary", createdByUserId: bobId);
        _documentRepository.GetByIdAsync(document.Id, Arg.Any<CancellationToken>()).Returns(document);

        var service = CreateService(new FakeAccessContext { Grants = [ReadGrant] });

        var result = await service.GetDocumentAsync(document.Id);

        result.CreatedBy.Should().Be("Bob");
        result.RawContent.Should().Be("raw content");
    }

    [Fact]
    public async Task CreateDocumentAsync_propagates_extraction_failures_as_a_document_extraction_exception()
    {
        var base64 = Convert.ToBase64String("not really a pdf"u8.ToArray());
        _pdfTextExtractor.ExtractTextAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new DocumentExtractionException("Could not extract text from the PDF: corrupt file.")));

        var service = CreateService(new FakeAccessContext { Grants = [ReadWriteGrant] });

        var act = async () => await service.CreateDocumentAsync("report.pdf", "pdf", base64, null, containerTag: null);

        await act.Should().ThrowAsync<DocumentExtractionException>();
    }
}
