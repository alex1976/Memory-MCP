using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using MemoryMcp.Application.Memories;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MemoryMcp.Api.Tests;

[Collection(McpApiCollection.Name)]
public sealed class McpToolsEndToEndTests(McpApiFactory factory)
{
    private static readonly string[] ExpectedToolNames =
    [
        "whoAmI", "listSpaces", "listDocuments", "getDocument", "listMemories", "add_memory", "search_memory",
    ];

    [Fact]
    public async Task Unauthenticated_request_is_rejected()
    {
        using var httpClient = factory.CreateClient();
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var response = await httpClient.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Health_endpoint_is_anonymous_and_reports_database_reachability()
    {
        using var httpClient = factory.CreateClient();

        var response = await httpClient.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("healthy");
    }

    [Fact]
    public async Task All_seven_tools_are_registered_and_exercisable_end_to_end()
    {
        using var httpClient = factory.CreateClient();
        httpClient.DefaultRequestHeaders.Add("X-Api-Key", factory.RawApiKey);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(httpClient.BaseAddress!, "/mcp") },
            httpClient);

        var client = await McpClient.CreateAsync(transport);

        var tools = await client.ListToolsAsync();
        tools.Select(t => t.Name).Should().Contain(ExpectedToolNames);

        var whoAmI = await client.CallToolAsync("whoAmI", new Dictionary<string, object?>());
        (whoAmI.IsError ?? false).Should().BeFalse();

        var addResult = await client.CallToolAsync(
            "add_memory", new Dictionary<string, object?> { ["content"] = "The sky is blue" });
        (addResult.IsError ?? false).Should().BeFalse();

        var searchResult = await client.CallToolAsync(
            "search_memory", new Dictionary<string, object?> { ["query"] = "sky color" });
        (searchResult.IsError ?? false).Should().BeFalse();

        var listDocuments = await client.CallToolAsync("listDocuments", new Dictionary<string, object?>());
        (listDocuments.IsError ?? false).Should().BeFalse();

        var listMemories = await client.CallToolAsync("listMemories", new Dictionary<string, object?>());
        (listMemories.IsError ?? false).Should().BeFalse();

        var listSpaces = await client.CallToolAsync("listSpaces", new Dictionary<string, object?>());
        (listSpaces.IsError ?? false).Should().BeFalse();

        await client.DisposeAsync();
    }

    [Fact]
    public async Task Search_memory_supports_keyword_and_category_filtering()
    {
        using var httpClient = factory.CreateClient();
        httpClient.DefaultRequestHeaders.Add("X-Api-Key", factory.RawApiKey);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(httpClient.BaseAddress!, "/mcp") },
            httpClient);

        var client = await McpClient.CreateAsync(transport);

        var addResult = await client.CallToolAsync(
            "add_memory",
            new Dictionary<string, object?> { ["content"] = "The invoice due date is the 5th", ["category"] = "finance" });
        (addResult.IsError ?? false).Should().BeFalse();

        var keywordSearch = await client.CallToolAsync(
            "search_memory", new Dictionary<string, object?> { ["keyword"] = "invoice", ["includeProfile"] = false });
        (keywordSearch.IsError ?? false).Should().BeFalse();

        var categorySearch = await client.CallToolAsync(
            "search_memory", new Dictionary<string, object?> { ["category"] = "finance", ["includeProfile"] = false });
        (categorySearch.IsError ?? false).Should().BeFalse();

        await client.DisposeAsync();
    }

    [Fact]
    public async Task Search_memory_without_query_keyword_or_category_returns_a_tool_error()
    {
        using var httpClient = factory.CreateClient();
        httpClient.DefaultRequestHeaders.Add("X-Api-Key", factory.RawApiKey);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(httpClient.BaseAddress!, "/mcp") },
            httpClient);

        var client = await McpClient.CreateAsync(transport);

        var result = await client.CallToolAsync("search_memory", new Dictionary<string, object?>());

        (result.IsError ?? false).Should().BeTrue();

        await client.DisposeAsync();
    }

    [Fact]
    public async Task Search_memory_returns_related_memories_for_facts_linked_by_a_graph_edge()
    {
        using var httpClient = factory.CreateClient();
        httpClient.DefaultRequestHeaders.Add("X-Api-Key", factory.RawApiKey);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(httpClient.BaseAddress!, "/mcp") },
            httpClient);

        var client = await McpClient.CreateAsync(transport);

        var first = await client.CallToolAsync(
            "add_memory", new Dictionary<string, object?> { ["content"] = "Alex is a PM at Stripe" });
        (first.IsError ?? false).Should().BeFalse();

        // FakeFactExtractor (registered in McpApiFactory) relates every new fact to all candidate
        // memories via "Extends", so this second save creates a graph edge back to the first memory.
        var second = await client.CallToolAsync(
            "add_memory", new Dictionary<string, object?> { ["content"] = "Alex now leads a team of 5 at Stripe" });
        (second.IsError ?? false).Should().BeFalse();

        var searchResult = await client.CallToolAsync(
            "search_memory", new Dictionary<string, object?> { ["query"] = "Alex's job", ["includeProfile"] = false });
        (searchResult.IsError ?? false).Should().BeFalse();

        var deserializeOptions = new JsonSerializerOptions(JsonSerializerOptions.Web);
        deserializeOptions.Converters.Add(new JsonStringEnumConverter());

        var json = searchResult.Content.OfType<TextContentBlock>().First().Text;
        var parsed = JsonSerializer.Deserialize<SearchMemoryResult>(json, deserializeOptions);

        parsed!.Matches.Should().Contain(m => m.RelatedMemories != null && m.RelatedMemories.Count > 0);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task Unknown_containerTag_returns_a_tool_error_instead_of_a_500()
    {
        using var httpClient = factory.CreateClient();
        httpClient.DefaultRequestHeaders.Add("X-Api-Key", factory.RawApiKey);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(httpClient.BaseAddress!, "/mcp") },
            httpClient);

        var client = await McpClient.CreateAsync(transport);

        var result = await client.CallToolAsync(
            "listDocuments", new Dictionary<string, object?> { ["containerTag"] = "does-not-exist" });

        (result.IsError ?? false).Should().BeTrue();

        await client.DisposeAsync();
    }
}
