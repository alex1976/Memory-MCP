using System.ComponentModel;
using MemoryMcp.Application.Abstractions;
using MemoryMcp.Application.Documents;
using ModelContextProtocol.Server;

namespace MemoryMcp.Api.Tools;

[McpServerToolType]
public sealed class DocumentTools(IDocumentService documentService)
{
    [McpServerTool(Name = "listDocuments")]
    [Description("Lists source documents stored in a space, paginated.")]
    public Task<PagedResult<DocumentSummaryDto>> ListDocuments(
        [Description("Page number, 1-based. Defaults to 1.")] int page = 1,
        [Description("Items per page, max 50. Defaults to 10.")] int limit = 10,
        [Description("Space key; defaults to the API key's active space.")] string? containerTag = null,
        CancellationToken cancellationToken = default) =>
        McpExecution.RunAsync(() => documentService.ListDocumentsAsync(containerTag, page, limit, cancellationToken));

    [McpServerTool(Name = "getDocument")]
    [Description("Reads the metadata and available content of a single document.")]
    public Task<DocumentDetailDto> GetDocument(
        [Description("Document id.")] Guid documentId,
        CancellationToken cancellationToken = default) =>
        McpExecution.RunAsync(() => documentService.GetDocumentAsync(documentId, cancellationToken));
}
