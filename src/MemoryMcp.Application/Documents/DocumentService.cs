using MemoryMcp.Application.Abstractions;
using MemoryMcp.Domain;

namespace MemoryMcp.Application.Documents;

public sealed class DocumentService(
    IDocumentRepository documentRepository,
    IUserRepository userRepository,
    ICurrentAccessContext accessContext,
    IUnitOfWork unitOfWork,
    IPdfTextExtractor pdfTextExtractor) : IDocumentService
{
    private const string PdfDocType = "pdf";

    public async Task<PagedResult<DocumentSummaryDto>> ListDocumentsAsync(
        string? containerTag, int page, int limit, CancellationToken cancellationToken = default)
    {
        var grant = accessContext.RequireSpace(containerTag, AccessLevel.Read);

        var (clampedPage, clampedLimit) = Paging.Clamp(page, limit);
        var (items, totalCount) = await documentRepository.ListAsync(grant.SpaceId, clampedPage, clampedLimit, cancellationToken);

        // Every document in the space is listed regardless of who stored it — authorship is reported,
        // not used as a filter.
        var attribution = await LoadAttributionAsync(items, cancellationToken);
        var dtos = items.Select(d => ToSummary(d, attribution)).ToList();

        return new PagedResult<DocumentSummaryDto>(dtos, clampedPage, clampedLimit, totalCount);
    }

    public async Task<DocumentDetailDto> GetDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var document = await documentRepository.GetByIdAsync(documentId, cancellationToken)
            ?? throw new EntityNotFoundException($"Document '{documentId}' was not found.");

        accessContext.RequireSpaceAccess(document.SpaceId, AccessLevel.Read, $"document '{documentId}'");

        var attribution = await LoadAttributionAsync([document], cancellationToken);

        return new DocumentDetailDto(
            document.Id, document.Title, document.DocType, document.Status.ToString(),
            document.Summary, document.RawContent, document.CreatedAt, document.UpdatedAt,
            CreatedByUserId: document.CreatedByUserId,
            CreatedBy: attribution.DisplayName(document.CreatedByUserId),
            UpdatedByUserId: document.UpdatedByUserId,
            UpdatedBy: attribution.DisplayName(document.UpdatedByUserId));
    }

    public async Task<DocumentSummaryDto> CreateDocumentAsync(
        string title, string docType, string content, string? summary, string? containerTag, CancellationToken cancellationToken = default)
    {
        var grant = accessContext.RequireSpace(containerTag, AccessLevel.ReadWrite);
        var user = accessContext.User;

        var rawContent = content;
        if (string.Equals(docType, PdfDocType, StringComparison.OrdinalIgnoreCase))
        {
            rawContent = await ExtractPdfTextAsync(content, cancellationToken);
        }

        var document = new Document(grant.SpaceId, title, docType, rawContent: rawContent, summary: summary, createdByUserId: user.Id);
        document.MarkProcessed(summary, byUserId: user.Id);

        documentRepository.Add(document);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // The author is the caller, so no lookup is needed to name them.
        return new DocumentSummaryDto(
            document.Id, document.Title, document.DocType, document.Status.ToString(), document.Summary,
            document.CreatedAt, document.UpdatedAt,
            CreatedByUserId: user.Id, CreatedBy: user.DisplayName,
            UpdatedByUserId: user.Id, UpdatedBy: user.DisplayName);
    }

    private Task<UserAttribution> LoadAttributionAsync(
        IReadOnlyList<Document> documents, CancellationToken cancellationToken) =>
        UserAttribution.LoadAsync(
            userRepository,
            documents.SelectMany(d => new[] { d.CreatedByUserId, d.UpdatedByUserId }),
            cancellationToken);

    private static DocumentSummaryDto ToSummary(Document document, UserAttribution attribution) =>
        new(document.Id, document.Title, document.DocType, document.Status.ToString(), document.Summary,
            document.CreatedAt, document.UpdatedAt,
            CreatedByUserId: document.CreatedByUserId,
            CreatedBy: attribution.DisplayName(document.CreatedByUserId),
            UpdatedByUserId: document.UpdatedByUserId,
            UpdatedBy: attribution.DisplayName(document.UpdatedByUserId));

    private async Task<string> ExtractPdfTextAsync(string base64Content, CancellationToken cancellationToken)
    {
        byte[] pdfBytes;
        try
        {
            pdfBytes = Convert.FromBase64String(base64Content);
        }
        catch (FormatException)
        {
            throw new DocumentExtractionException("PDF content must be base64-encoded.");
        }

        return await pdfTextExtractor.ExtractTextAsync(pdfBytes, cancellationToken);
    }
}
