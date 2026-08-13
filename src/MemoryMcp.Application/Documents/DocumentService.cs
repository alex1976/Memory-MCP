using MemoryMcp.Application.Abstractions;
using MemoryMcp.Domain;

namespace MemoryMcp.Application.Documents;

public sealed class DocumentService(
    IDocumentRepository documentRepository,
    ICurrentAccessContext accessContext,
    IUnitOfWork unitOfWork,
    IPdfTextExtractor pdfTextExtractor) : IDocumentService
{
    private const string PdfDocType = "pdf";

    public async Task<PagedResult<DocumentSummaryDto>> ListDocumentsAsync(
        string? containerTag, int page, int limit, CancellationToken cancellationToken = default)
    {
        var grant = accessContext.ResolveGrant(containerTag) ?? throw new SpaceNotFoundException(containerTag);
        if (!accessContext.HasAccess(grant, AccessLevel.Read))
        {
            throw new AccessDeniedException($"The current API key does not have read access to space '{grant.SpaceKey}'.");
        }

        var (clampedPage, clampedLimit) = Paging.Clamp(page, limit);
        var (items, totalCount) = await documentRepository.ListAsync(grant.SpaceId, clampedPage, clampedLimit, cancellationToken);

        var dtos = items
            .Select(d => new DocumentSummaryDto(d.Id, d.Title, d.DocType, d.Status.ToString(), d.Summary, d.CreatedAt, d.UpdatedAt))
            .ToList();

        return new PagedResult<DocumentSummaryDto>(dtos, clampedPage, clampedLimit, totalCount);
    }

    public async Task<DocumentDetailDto> GetDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var document = await documentRepository.GetByIdAsync(documentId, cancellationToken)
            ?? throw new EntityNotFoundException($"Document '{documentId}' was not found.");

        var grant = accessContext.Grants.FirstOrDefault(g => g.SpaceId == document.SpaceId);
        if (!accessContext.HasAccess(grant, AccessLevel.Read))
        {
            throw new AccessDeniedException($"The current API key does not have read access to document '{documentId}'.");
        }

        return new DocumentDetailDto(
            document.Id, document.Title, document.DocType, document.Status.ToString(),
            document.Summary, document.RawContent, document.CreatedAt, document.UpdatedAt);
    }

    public async Task<DocumentSummaryDto> CreateDocumentAsync(
        string title, string docType, string content, string? summary, string? containerTag, CancellationToken cancellationToken = default)
    {
        var grant = accessContext.ResolveGrant(containerTag) ?? throw new SpaceNotFoundException(containerTag);
        if (!accessContext.HasAccess(grant, AccessLevel.ReadWrite))
        {
            throw new AccessDeniedException($"The current API key does not have write access to space '{grant.SpaceKey}'.");
        }

        var rawContent = content;
        if (string.Equals(docType, PdfDocType, StringComparison.OrdinalIgnoreCase))
        {
            rawContent = await ExtractPdfTextAsync(content, cancellationToken);
        }

        var document = new Document(grant.SpaceId, title, docType, rawContent: rawContent, summary: summary);
        document.MarkProcessed(summary);

        documentRepository.Add(document);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new DocumentSummaryDto(document.Id, document.Title, document.DocType, document.Status.ToString(), document.Summary, document.CreatedAt, document.UpdatedAt);
    }

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
