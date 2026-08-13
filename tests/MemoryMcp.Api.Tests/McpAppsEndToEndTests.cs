using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using MemoryMcp.Application.Documents;
using MemoryMcp.Application.Memories;
using MemoryMcp.Application.Spaces;
using MemoryMcp.Domain;
using MemoryMcp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MemoryMcp.Api.Tests;

[Collection(McpApiCollection.Name)]
public sealed class McpAppsEndToEndTests(McpApiFactory factory)
{
    private const string HtmlMimeType = "text/html;profile=mcp-app";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerOptions.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static readonly string[] ExpectedUiResourceUris =
        ["ui://select-space", "ui://guided-save", "ui://upload-file", "ui://memory-graph"];

    private static readonly string[] ExpectedAppToolNames =
        ["setActiveSpace", "create_document", "select_space_ui", "guided_save_ui", "upload_file_ui", "memory_graph_ui"];

    // Same hand-written minimal single-page PDF as MemoryMcp.Infrastructure.Tests/PdfTextExtractorTests.cs —
    // PdfPig recovers the page tree even with approximate (not byte-exact) xref offsets.
    private const string MinimalPdf = """
        %PDF-1.1
        1 0 obj  << /Type /Catalog /Pages 2 0 R >> endobj
        2 0 obj  << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj
        3 0 obj  << /Type /Page /Parent 2 0 R /Resources << /Font << /F1 4 0 R >> >> /MediaBox [0 0 300 144] /Contents 5 0 R >> endobj
        4 0 obj  << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> endobj
        5 0 obj  << /Length 44 >>
        stream
        BT /F1 18 Tf 0 0 Td (Hello World) Tj ET
        endstream
        endobj
        xref
        0 6
        0000000000 65535 f
        0000000018 00000 n
        0000000077 00000 n
        0000000178 00000 n
        0000000457 00000 n
        0000000496 00000 n
        trailer  << /Root 1 0 R /Size 6 >>
        startxref
        625
        %%EOF
        """;

    private async Task<McpClient> CreateClientAsync()
    {
        var httpClient = factory.CreateClient();
        httpClient.DefaultRequestHeaders.Add("X-Api-Key", factory.RawApiKey);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(httpClient.BaseAddress!, "/mcp") },
            httpClient);

        return await McpClient.CreateAsync(transport);
    }

    [Fact]
    public async Task The_widget_tools_and_ui_resources_are_registered()
    {
        var client = await CreateClientAsync();

        var tools = await client.ListToolsAsync();
        tools.Select(t => t.Name).Should().Contain(ExpectedAppToolNames);

        var resources = await client.ListResourcesAsync();
        resources.Select(r => r.Uri).Should().Contain([.. ExpectedUiResourceUris, "memory-mcp://graph"]);
        foreach (var uri in ExpectedUiResourceUris)
        {
            resources.Should().ContainSingle(r => r.Uri == uri && r.MimeType == HtmlMimeType);
        }

        await client.DisposeAsync();
    }

    [Theory]
    [InlineData("select_space_ui", "ui://select-space")]
    [InlineData("guided_save_ui", "ui://guided-save")]
    [InlineData("upload_file_ui", "ui://upload-file")]
    [InlineData("memory_graph_ui", "ui://memory-graph")]
    public async Task Widget_opener_tool_declares_its_ui_resource_via_meta(string toolName, string expectedResourceUri)
    {
        // This is exactly the field an MCP Apps-capable host reads to decide which resource to render
        // as an iframe after calling the opener tool — if it's missing/wrong, the host has nothing to
        // render even though the tool call itself succeeds.
        var client = await CreateClientAsync();

        var tools = await client.ListToolsAsync();
        var tool = tools.Should().ContainSingle(t => t.Name == toolName).Subject;

        var uiMeta = tool.ProtocolTool.Meta?["ui"];
        uiMeta.Should().NotBeNull($"tool '{toolName}' should carry _meta.ui set by [McpAppUi]/.WithMcpApps()");
        uiMeta!["resourceUri"]!.GetValue<string>().Should().Be(expectedResourceUri);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task Each_ui_resource_reads_back_html_content()
    {
        var client = await CreateClientAsync();

        foreach (var uri in ExpectedUiResourceUris)
        {
            var result = await client.ReadResourceAsync(uri);
            var text = result.Contents.OfType<TextResourceContents>().First().Text;
            text.Should().Contain("<html", $"resource {uri} should serve an HTML document");
        }

        await client.DisposeAsync();
    }

    [Fact]
    public async Task SetActiveSpace_switches_the_default_space_for_the_api_key()
    {
        var secondSpaceKey = $"e2e-second-{Guid.NewGuid():N}";
        await SeedSecondSpaceForCurrentApiKeyAsync(secondSpaceKey);

        var client = await CreateClientAsync();

        var result = await client.CallToolAsync(
            "setActiveSpace", new Dictionary<string, object?> { ["spaceKey"] = secondSpaceKey });
        (result.IsError ?? false).Should().BeFalse();

        var json = result.Content.OfType<TextContentBlock>().First().Text;
        var spaces = JsonSerializer.Deserialize<List<SpaceSummaryDto>>(json, JsonOptions);
        spaces.Should().ContainSingle(s => s.Key == secondSpaceKey && s.IsDefault);
        spaces.Should().ContainSingle(s => s.Key == factory.SpaceKey && !s.IsDefault);

        var whoAmI = await client.CallToolAsync("whoAmI", new Dictionary<string, object?>());
        var whoAmIJson = whoAmI.Content.OfType<TextContentBlock>().First().Text;
        var whoAmIResult = JsonSerializer.Deserialize<WhoAmIResult>(whoAmIJson, JsonOptions);
        whoAmIResult!.ActiveSpaceKey.Should().Be(secondSpaceKey);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task Create_document_persists_a_document_independently_of_add_memory()
    {
        var client = await CreateClientAsync();
        const string content = "Uploaded notes about the quarterly roadmap";

        var createResult = await client.CallToolAsync(
            "create_document",
            new Dictionary<string, object?> { ["title"] = "roadmap.md", ["docType"] = "markdown", ["content"] = content });
        (createResult.IsError ?? false).Should().BeFalse();

        var docJson = createResult.Content.OfType<TextContentBlock>().First().Text;
        var doc = JsonSerializer.Deserialize<DocumentSummaryDto>(docJson, JsonOptions);
        doc!.Title.Should().Be("roadmap.md");

        var listDocuments = await client.CallToolAsync("listDocuments", new Dictionary<string, object?>());
        var listJson = listDocuments.Content.OfType<TextContentBlock>().First().Text;
        listJson.Should().Contain("roadmap.md");

        // create_document only stores the document — memory extraction requires a separate add_memory call.
        var addResult = await client.CallToolAsync("add_memory", new Dictionary<string, object?> { ["content"] = content });
        (addResult.IsError ?? false).Should().BeFalse();

        var searchResult = await client.CallToolAsync(
            "search_memory", new Dictionary<string, object?> { ["keyword"] = "roadmap", ["includeProfile"] = false });
        var searchJson = searchResult.Content.OfType<TextContentBlock>().First().Text;
        searchJson.Should().Contain(content);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task Create_document_extracts_text_from_a_base64_encoded_pdf()
    {
        var client = await CreateClientAsync();
        var base64Pdf = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(MinimalPdf));

        var createResult = await client.CallToolAsync(
            "create_document",
            new Dictionary<string, object?> { ["title"] = "report.pdf", ["docType"] = "pdf", ["content"] = base64Pdf });
        (createResult.IsError ?? false).Should().BeFalse();

        var docJson = createResult.Content.OfType<TextContentBlock>().First().Text;
        var doc = JsonSerializer.Deserialize<DocumentSummaryDto>(docJson, JsonOptions);
        doc!.DocType.Should().Be("pdf");

        var getResult = await client.CallToolAsync(
            "getDocument", new Dictionary<string, object?> { ["documentId"] = doc.Id });
        var detailJson = getResult.Content.OfType<TextContentBlock>().First().Text;
        var detail = JsonSerializer.Deserialize<DocumentDetailDto>(detailJson, JsonOptions);

        detail!.RawContent.Should().Contain("Hello World");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task Create_document_returns_a_tool_error_for_invalid_base64_pdf_content()
    {
        var client = await CreateClientAsync();

        var result = await client.CallToolAsync(
            "create_document",
            new Dictionary<string, object?> { ["title"] = "bad.pdf", ["docType"] = "pdf", ["content"] = "not-base64!!" });

        (result.IsError ?? false).Should().BeTrue();

        await client.DisposeAsync();
    }

    [Fact]
    public async Task Memory_graph_resource_reports_nodes_and_relation_typed_edges()
    {
        var client = await CreateClientAsync();

        var first = await client.CallToolAsync(
            "add_memory", new Dictionary<string, object?> { ["content"] = "Graph widget test: Jordan joined the design team" });
        (first.IsError ?? false).Should().BeFalse();

        // FakeFactExtractor (registered in McpApiFactory) relates every new fact to all candidates via "Extends".
        var second = await client.CallToolAsync(
            "add_memory", new Dictionary<string, object?> { ["content"] = "Graph widget test: Jordan now leads the design team" });
        (second.IsError ?? false).Should().BeFalse();

        var graphResult = await client.ReadResourceAsync("memory-mcp://graph");
        var json = graphResult.Contents.OfType<TextResourceContents>().First().Text;
        var graph = JsonSerializer.Deserialize<SpaceGraphDto>(json, JsonOptions);

        graph!.Nodes.Should().Contain(n => n.Text.Contains("Jordan"));
        graph.Edges.Should().Contain(e => e.RelationType == RelationType.Extends);

        await client.DisposeAsync();
    }

    private async Task SeedSecondSpaceForCurrentApiKeyAsync(string spaceKey)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        var apiKeyId = await db.ApiKeys
            .Where(k => k.KeyHash == ApiKeyHasher.Hash(factory.RawApiKey))
            .Select(k => k.Id)
            .SingleAsync();

        var space = new Space(spaceKey, "Second E2E Space");
        var grant = new ApiKeySpaceGrant(apiKeyId, space.Id, AccessLevel.ReadWrite, isDefault: false);

        db.Spaces.Add(space);
        db.ApiKeySpaceGrants.Add(grant);
        await db.SaveChangesAsync();
    }
}
